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

using sk0ya.Loomo.Core.Completion;

namespace sk0ya.Loomo.Completion.Host;

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
/// 入力の先読みを作る FIM 専用の常駐エンジン（llama.cpp / GGUF / CPU）。
/// <b>このクラスは本体とは別のプロセスで動く</b>——打鍵のたびに呼ばれるものを、UI スレッドと
/// 同じプロセスに置いてはいけない（CPU もメモリ帯域も GC も分け合うことになる）。
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
public sealed class FimEngine : IDisposable
{
    /// <summary>
    /// 1 回の decode に載せるトークン数の上限。小さいほど<b>打鍵で中断しやすい</b>——
    /// decode は途中で止められないので、この粒度がそのままキャンセルの応答時間になる。
    /// 効率だけなら大きい方が良いが、ここでは入力を待たせない方を採る。
    /// </summary>
    private const int PrefillChunkTokens = 64;

    /// <summary>モデルに持たせる窓。prefix 30 行＋suffix 3 行なら 400 トークン前後で収まる。</summary>
    private const int ContextSize = 2048;

    /// <summary>1 件の生成をここで諦める。長引いた提案は、出る頃には手が先へ進んでいて
    /// どのみち捨てられる——待ち続けるだけ CPU を取られ、入力が重くなる。</summary>
    private static readonly TimeSpan GenerateBudget = TimeSpan.FromMilliseconds(1500);

    private readonly string _modelPath;
    private readonly int _decodeThreads;
    private readonly int _prefillThreads;

    /// <param name="modelPath">GGUF モデルのパス。</param>
    /// <param name="decodeThreads">生成に使うスレッド数（0 以下なら 2）。</param>
    /// <param name="prefillThreads">前処理に使うスレッド数（0 以下ならコア数 − 2）。</param>
    public FimEngine(string modelPath, int decodeThreads = 0, int prefillThreads = 0)
    {
        _modelPath = modelPath;
        _decodeThreads = decodeThreads > 0 ? decodeThreads : 2;
        _prefillThreads = prefillThreads > 0 ? prefillThreads : Math.Max(1, Environment.ProcessorCount - 2);
    }

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
    /// 依頼を 1 件こなす。出せないときは <see cref="FimProtocol.Response.Text"/> が null——
    /// 失敗も「出せなかった」に畳む。本体にできることは、どちらでも「何も出さない」だけだから。
    ///
    /// <para>呼び出しは生成スレッド 1 本からに限る（KV キャッシュを持つので同時に走らせられない）。</para>
    /// </summary>
    public FimProtocol.Response Complete(in FimProtocol.Request request, CancellationToken ct)
    {
        if (_disposed) return new FimProtocol.Response(request.Id, Error: "破棄済み");
        if (!File.Exists(_modelPath)) return new FimProtocol.Response(request.Id, Error: $"モデルが無い: {_modelPath}");

        try
        {
            EnsureLoaded();
            if (_context is null || _weights is null || _batch is null)
                return new FimProtocol.Response(request.Id, Error: "モデルを読み込めない");

            var raw = Generate(request.Prompt, Math.Max(1, request.MaxTokens), ct);
            var t = LastTiming;
            return new FimProtocol.Response(
                request.Id, raw, t.ReusedTokens, t.PrefillTokens, t.PrefillMs, t.GeneratedTokens, t.GenerateMs);
        }
        catch (OperationCanceledException)
        {
            return new FimProtocol.Response(request.Id);   // 打鍵で追い越された。正常な取り消し。
        }
        catch (Exception ex)
        {
            return new FimProtocol.Response(request.Id, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>ワーカーの覚え書きは stderr へ。stdout は応答専用なので混ぜない。</summary>
    private static void Log(string message)
    {
        try { Console.Error.WriteLine($"[fim] {message}"); } catch { }
    }

    private void EnsureLoaded()
    {
        if (_context is not null && _loadedPath == _modelPath) return;

        Unload();

        Log($"モデルを読み込む: {_modelPath} (decode {_decodeThreads} / prefill {_prefillThreads} スレッド)");
        var parameters = new ModelParams(_modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = 0,
            BatchSize = 512,
            Threads = _decodeThreads,
            BatchThreads = _prefillThreads,
        };

        _weights = LLamaWeights.LoadFromFile(parameters);
        _context = _weights.CreateContext(parameters);
        _batch = new LLamaBatch();
        _loadedPath = _modelPath;
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
        // prefill が長引くのは KV が空のとき（＝最初の 1 回）だけで、それは次回以降のための投資。
        // ここまで来たら生成まで進む——数トークンぶんの上乗せは安く、捨てると払った分が無駄になる。

        var builder = new StringBuilder(64);
        using var sampler = new GreedySamplingPipeline();   // 先読みは毎回同じ答えが返る方がよい
        var decoder = new StreamingTokenDecoder(context);
        int produced = 0;
        int position = n;

        var generateSw = Stopwatch.StartNew();
        while (produced < maxTokens)
        {
            ct.ThrowIfCancellationRequested();
            if (generateSw.Elapsed > GenerateBudget)
            {
                Log($"打ち切り: {generateSw.ElapsedMilliseconds}ms で {produced} トークン");
                break;
            }

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
    }
}
