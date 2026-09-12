using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Ai.Completion;

/// <summary>1 回の先読みにかかった時間の内訳。バーや診断に出す。</summary>
/// <param name="ReusedTokens">KV から再利用できたトークン数。</param>
/// <param name="PrefillTokens">送り直したトークン数。</param>
/// <param name="PrefillMs">送り直しにかかった時間。</param>
/// <param name="GeneratedTokens">生成したトークン数。</param>
/// <param name="GenerateMs">生成にかかった時間。</param>
public readonly record struct FimTiming(
    int ReusedTokens, int PrefillTokens, long PrefillMs, int GeneratedTokens, long GenerateMs)
{
    public long TotalMs => PrefillMs + GenerateMs;
}

/// <summary>
/// 入力の先読みを作る FIM 専用の常駐エンジン（llama.cpp / GGUF / CPU）。チャット用の
/// <see cref="Clients.LlamaCppEngine"/> とはモデルもコンテキストも別に持つ——用途が違い、
/// 何より<b>打鍵のたびに呼ばれる</b>ので、チャットの生成と場所を取り合ってはいけない。
///
/// <para><b>速さは KV キャッシュの再利用がすべて。</b>実測（Ryzen 5 3500・6 コア・Qwen2.5-Coder 0.5B・Q8_0）:
/// 全部を prefill し直すと 335 トークンで約 1.0 秒だが、打鍵で変わるのは行末だけなので、
/// 前回と共通する接頭辞（実測 298 トークン）を残して差分（37 トークン）だけ送ると約 140ms。
/// 生成 3〜8 トークンを足して<b>打鍵あたり約 280ms</b>。この再利用が無いと 1 秒を超え、
/// 先読みとしては使いものにならない。</para>
///
/// <para>量子化は Q8_0 を使う。Q4_K_M でも速くならず（0.5B は演算律速で、メモリ帯域律速ではない）、
/// 出力だけが崩れた。</para>
/// </summary>
public sealed class FimCompletionEngine : IDisposable
{
    /// <summary>1 回の decode に載せるトークン数の上限（llama.cpp の n_batch 安全圏）。</summary>
    private const int PrefillChunkTokens = 256;

    /// <summary>モデルに持たせる窓。prefix 30 行＋suffix 3 行なら 400 トークン前後で収まる。</summary>
    private const int ContextSize = 2048;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private LLamaBatch? _batch;
    private string? _loadedPath;
    private bool _disposed;

    /// <summary>いま KV に入っているトークン列。次回の共通接頭辞を測るために持つ。</summary>
    private readonly List<int> _fed = new();

    /// <summary>直近の内訳。</summary>
    public FimTiming LastTiming { get; private set; }

    /// <summary>モデルを読み込み済みか。</summary>
    public bool IsLoaded => _context is not null;

    /// <summary>
    /// 先読みを 1 件作る。出せないときは null（設定が無効、モデルが無い、モデルが何も返さない、
    /// 取り消された、のいずれも null に畳む——入力の邪魔をしないことが最優先で、
    /// 失敗を呼び出し側に見せても打鍵中にできることは何もない）。
    /// </summary>
    /// <param name="settings">有効・モデルパス・前後の行数。</param>
    /// <param name="lines">バッファ全行。</param>
    /// <param name="line">キャレット行。</param>
    /// <param name="column">キャレット桁。</param>
    public async Task<string?> CompleteAsync(
        InlineCompletionSettings settings, IReadOnlyList<string> lines, int line, int column,
        CancellationToken ct)
    {
        if (_disposed) return Refuse("破棄済み");
        if (settings is null || !settings.Enabled) return Refuse("設定が無効");
        if (string.IsNullOrWhiteSpace(settings.ModelPath)) return Refuse("モデル未設定");
        if (!File.Exists(settings.ModelPath)) return Refuse($"モデルが無い: {settings.ModelPath}");
        if (lines is null || lines.Count == 0) return Refuse("本文が空");

        // 打鍵のたびに呼ばれる。前の生成が終わるまで待つが、待っている間にこの要求自体が
        // 古くなればキャンセルされて抜ける（呼び出し側が新しい要求のたびに前のを取り消す）。
        //
        // ここを「取れなければ即あきらめる」にしていたときは、生成 1 回ぶん（約 270ms）の間に
        // 来た要求が全部捨てられ、<b>最新の要求まで道連れ</b>になっていた。打鍵を続けるほど
        // 何も出なくなるという、一番ありがたくない壊れ方をする。
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }

        try
        {
            EnsureLoaded(settings);
            if (_context is null || _weights is null || _batch is null) return null;

            var prompt = FimPrompt.Build(lines, line, column, settings.PrefixLines, settings.SuffixLines);
            var raw = Generate(prompt, Math.Max(1, settings.MaxTokens), ct);
            if (raw is null) return Refuse("生成が空");

            var current = lines[Math.Clamp(line, 0, lines.Count - 1)];
            int col = Math.Clamp(column, 0, current.Length);
            var cleaned = FimCandidateFilter.Clean(raw, current[..col], current[col..]);
            Log(cleaned is null
                ? $"落とした: 生「{raw.ReplaceLineEndings(" ")}」 ({LastTiming.TotalMs}ms)"
                : $"採用: 「{cleaned}」 ({LastTiming.TotalMs}ms 再利用{LastTiming.ReusedTokens}/送り直し{LastTiming.PrefillTokens})");
            return cleaned;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log($"失敗: {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"FimCompletionEngine: {ex}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>LOOMO_INLINE_DIAG=1 のときだけ %TEMP%\loomo-inline-debug.log へ書く。
    /// 先読みは黙って諦めるのが正しい振る舞いなので、諦めた理由を残さないと外から追えない。</summary>
    private static readonly bool s_diag =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_INLINE_DIAG"), "1", StringComparison.Ordinal);

    private static void Log(string message)
    {
        if (!s_diag) return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "loomo-inline-debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine);
        }
        catch { }
    }

    private static string? Refuse(string reason)
    {
        Log($"出さない: {reason}");
        return null;
    }

    private void EnsureLoaded(InlineCompletionSettings settings)
    {
        if (_context is not null && _loadedPath == settings.ModelPath) return;

        Log($"モデルを読み込む: {settings.ModelPath}");
        Unload();

        int threads = settings.Threads > 0 ? settings.Threads : Math.Max(1, Environment.ProcessorCount);
        var parameters = new ModelParams(settings.ModelPath!)
        {
            ContextSize = ContextSize,
            GpuLayerCount = 0,
            BatchSize = 512,
            Threads = threads,
            BatchThreads = threads,
        };

        _weights = LLamaWeights.LoadFromFile(parameters);
        _context = _weights.CreateContext(parameters);
        _batch = new LLamaBatch();
        _loadedPath = settings.ModelPath;
        _fed.Clear();
        Log("モデルの読み込み完了");
    }

    private string? Generate(string prompt, int maxTokens, CancellationToken ct)
    {
        var context = _context!;
        var weights = _weights!;
        var batch = _batch!;

        var tokens = weights.NativeHandle.Tokenize(prompt, addBos: true, special: true, Encoding.UTF8);
        int n = tokens.Length;
        if (n == 0 || n >= ContextSize) return null;

        var ints = new int[n];
        for (int i = 0; i < n; i++) ints[i] = (int)tokens[i];

        // 前回と共通する接頭辞は KV に残す。末尾 1 トークンは必ず送り直す
        // （logits を取るために decode させる必要がある）。
        int common = 0;
        while (common < _fed.Count && common < n && _fed[common] == ints[common]) common++;
        int reuse = Math.Max(0, Math.Min(common, n - 1));
        if (reuse < _fed.Count)
        {
            context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, reuse, -1);
            _fed.RemoveRange(reuse, _fed.Count - reuse);
        }

        var prefillSw = Stopwatch.StartNew();
        int lastLogit = Feed(context, batch, tokens, ints, reuse, n, ct);
        prefillSw.Stop();
        if (lastLogit < 0) return null;

        var builder = new StringBuilder(64);
        using var sampler = new GreedySamplingPipeline();   // 先読みは毎回同じ答えが返る方がよい
        var decoder = new StreamingTokenDecoder(context);
        int produced = 0;
        int position = n;

        var generateSw = Stopwatch.StartNew();
        while (produced < maxTokens)
        {
            ct.ThrowIfCancellationRequested();

            var token = sampler.Sample(context.NativeHandle, lastLogit);
            if (token.IsEndOfGeneration(weights.NativeHandle)) break;

            decoder.Add(token);
            var piece = decoder.Read();
            builder.Append(piece);
            produced++;
            if (piece.Contains('\n')) break;   // 1 行しか出さない

            batch.Clear();
            lastLogit = batch.Add(token, position++, LLamaSeqId.Zero, true);
            if (context.Decode(batch) != DecodeResult.Ok) break;
        }
        generateSw.Stop();

        // 生成ぶんの KV は捨てる。次の打鍵で使えるのはプロンプト部分だけで、
        // 残したままだと共通接頭辞の計算とずれる。
        context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, n, -1);

        LastTiming = new FimTiming(reuse, n - reuse, prefillSw.ElapsedMilliseconds, produced, generateSw.ElapsedMilliseconds);
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>[start, n) を分割して decode する。末尾トークンの logits の位置を返す。</summary>
    private int Feed(
        LLamaContext context, LLamaBatch batch, LLamaToken[] tokens, int[] ints, int start, int end,
        CancellationToken ct)
    {
        batch.Clear();   // 前回の生成ループの残りを持ち越すと位置が重なって decode が落ちる
        int lastLogit = -1;
        int inBatch = 0;

        for (int p = start; p < end; p++)
        {
            ct.ThrowIfCancellationRequested();

            bool wantLogits = p == end - 1;
            int index = batch.Add(tokens[p], p, LLamaSeqId.Zero, wantLogits);
            if (wantLogits) lastLogit = index;
            _fed.Add(ints[p]);
            inBatch++;

            if (inBatch >= PrefillChunkTokens || p == end - 1)
            {
                if (context.Decode(batch) != DecodeResult.Ok)
                {
                    // 窓溢れなど。KV の中身が信用できなくなるので捨てて、次回は全部送り直す。
                    ResetCache();
                    return -1;
                }
                batch.Clear();
                inBatch = 0;
            }
        }
        return lastLogit;
    }

    private void ResetCache()
    {
        _fed.Clear();
        try { _context?.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, 0, -1); } catch { }
    }

    private void Unload()
    {
        _batch = null;
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _loadedPath = null;
        _fed.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
        _gate.Dispose();
    }
}
