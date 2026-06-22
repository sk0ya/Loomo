using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// llama.cpp（LLamaSharp 同梱・CPU）を使った in-process ローカル推論エンジン。GGUF モデル
/// （例: Qwen3-4B-Q4_K_M.gguf）を駆動する。<see cref="OnnxGenAiEngine"/> と対構造で、同じ
/// <see cref="ILocalInferenceEngine"/> 契約（組み立て済みプロンプト → <see cref="TextDelta"/>＋
/// <see cref="AiUsageReported"/>）を実装する。比較を公平にするため、計測点（load / prefill / decode）・
/// イベント形・反復暴走ガード（<see cref="DecodeLoopGuards"/>）・KV プレフィックス再利用を ONNX エンジンと揃える。
///
/// 性能の要は ONNX 版と同じく <b>常駐 <see cref="LLamaContext"/> の KV キャッシュ再利用</b>。前ターンに KV へ
/// 載せたトークン列（<see cref="_fedTokens"/>）と今回プロンプトの最長共通接頭辞は再利用し、分岐分のみ
/// 再 prefill する（<see cref="SafeLLamaContextHandle.MemorySequenceRemove"/> で末尾の KV を捨て、差分を
/// <see cref="LLamaBatch"/> で decode）。CPU 実行では prefill が支配的なので、安定プレフィックス
/// （system + tools + 既存履歴）の再 prefill を初回 1 回に抑えるのが最大の効き手。
/// </summary>
public sealed class LlamaCppEngine : ILocalInferenceEngine, ILocalWarmableEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedPath;
    private int _ctxSize;
    private bool _disposed;

    // KV に現在入っているトークン列（プロンプト＋生成済み）。ターン間で最長共通接頭辞の再利用に使う。
    private readonly List<int> _fedTokens = new();

    // llama.cpp の n_batch（既定 512）を超える一括 decode は失敗するため、prefill はこの粒度で分割して
    // decode し、チャンク間で停止要求を確認する（ONNX 版と同じ狙い・値は n_batch 安全圏に収める）。
    private const int PrefillChunkTokens = 256;

    /// <summary>このエンジンが扱えるモデルパスか（GGUF ファイル）。ルータが拡張子で振り分ける。</summary>
    public static bool CanHandle(string modelPath) =>
        !string.IsNullOrWhiteSpace(modelPath) &&
        modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>モデル（重み＋コンテキスト）がロード済みか。</summary>
    public bool IsLoaded => _context is not null;

    /// <summary>
    /// モデルをロードし、安定プレフィックス（system＋tools）を常駐コンテキストへ prefill して KV を温める（暖機）。
    /// 狙いは ONNX 版（<see cref="OnnxGenAiEngine.PrimeAsync"/>）と同じ：(1) 重みのページイン、(2) 初回の実ターンが
    /// この KV プレフィックスを再利用して prefill を払い直さない。<paramref name="sampling"/> はサンプラを生成時に
    /// 作るため暖機には不要だが、インターフェース対称性のため受け取る。実ターンと同じ <paramref name="stablePrompt"/>／
    /// <paramref name="maxLength"/> で呼ぶこと（一致しないと最長共通接頭辞の再利用が効かない）。
    /// </summary>
    public async Task PrimeAsync(
        string modelPath, string stablePrompt, int maxLength, SamplingOptions sampling,
        CancellationToken ct, Action<string>? progress = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // ロード（数GB）と prefill は CPU 同期処理。呼び出し元（起動時は UI スレッド）をブロックしない。
            await Task.Run(() =>
            {
                progress?.Invoke("モデルをロードしています");
                LoadIfNeeded(modelPath, maxLength > 0 ? maxLength : 4096);
                var context = _context!;
                progress?.Invoke("プロンプトをトークン化しています");
                var promptTokens = context.Tokenize(stablePrompt, addBos: false, special: true);
                var promptInts = new int[promptTokens.Length];
                for (var i = 0; i < promptTokens.Length; i++) promptInts[i] = (int)promptTokens[i];
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"KVキャッシュを作成しています（prefill {promptTokens.Length} トークン）");
                Prefill(context, promptTokens, promptInts, ct);   // KV ＋ _fedTokens を満たす（重みのページイン込み）
                progress?.Invoke("ウォームアップを完了しています");
            }, ct);
        }
        catch (OperationCanceledException) { }
        finally { _gate.Release(); }
    }

    public async Task GenerateAsync(GenerationRequest request, ChannelWriter<AgentEvent> sink, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var loadSw = Stopwatch.StartNew();
        try
        {
            var maxLength = request.MaxLength > 0 ? request.MaxLength : 4096;
            var loadedNow = LoadIfNeeded(request.ModelPath, maxLength);
            var loadMs = loadedNow ? loadSw.Elapsed.TotalMilliseconds : 0;
            var context = _context!;
            var weights = _weights!;
            // 生成は同期ブロッキング（CPU）。UI/呼び出し側をブロックしないようバックグラウンドで回す。
            await Task.Run(() => RunSync(context, weights, request, maxLength, loadMs, sink, ct), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            sink.TryWrite(new AgentError(DescribeError(ex)));
        }
        finally
        {
            sink.TryComplete();
            _gate.Release();
        }
    }

    /// <summary>必要なら重み／コンテキストを（再）ロードする。重みを実際にロードしたら true
    /// （load コストは重みのロードのみ。コンテキスト再作成は安価）。呼び出しは _gate 内で行うこと。</summary>
    private bool LoadIfNeeded(string modelPath, int maxLength)
    {
        if (_weights is null || !string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            ValidateModelPath(modelPath);
            ResetContext();
            _weights?.Dispose();
            _weights = null; _loadedPath = null;

            var mp = MakeParams(modelPath, maxLength);
            _weights = LLamaWeights.LoadFromFile(mp);
            _loadedPath = modelPath;
            _context = _weights.CreateContext(mp);
            _ctxSize = maxLength;
            _fedTokens.Clear();
            return true;
        }

        // 重みは同一。コンテキストが無い／窓が小さすぎるなら作り直す（KV はクリア）。
        if (_context is null || _ctxSize < maxLength)
        {
            ResetContext();
            _context = _weights.CreateContext(MakeParams(modelPath, maxLength));
            _ctxSize = maxLength;
            _fedTokens.Clear();
        }
        return false;
    }

    private static ModelParams MakeParams(string modelPath, int maxLength) => new(modelPath)
    {
        ContextSize = (uint)maxLength,
        GpuLayerCount = 0,   // CPU 固定（ONNX 版と同条件で比較する）
    };

    private static void ValidateModelPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new InvalidOperationException(
                "モデルファイルが設定されていません。設定で GGUF モデルを指定するか、ダウンロードしてください。");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"GGUF モデルファイルが見つかりません: {modelPath}");
    }

    private void RunSync(
        LLamaContext context, LLamaWeights weights, GenerationRequest req, int maxLength, double loadMs,
        ChannelWriter<AgentEvent> sink, CancellationToken ct)
    {
        // ChatML の制御トークン（<|im_start|> 等）を特殊トークンとして解釈させる（special: true）。
        // Qwen の tokenizer は BOS を足さない（addBos: false）。
        var promptTokens = context.Tokenize(req.Prompt, addBos: false, special: true);
        var inputTokens = promptTokens.Length;
        var promptInts = new int[inputTokens];
        for (var i = 0; i < inputTokens; i++) promptInts[i] = (int)promptTokens[i];

        using var sampler = BuildSampler(req.Sampling);
        var decoder = new StreamingTokenDecoder(context);   // DecodeSpecialTokens=false（既定）＝制御トークンは本文に出さない

        var sw = Stopwatch.StartNew();

        // prefill。AppendTokens(=Decode) の前から計測し「最初のトークンが出るまで」を prefill とみなす。
        var lastLogit = Prefill(context, promptTokens, promptInts, ct);

        double? prefillMs = null;
        var outputTokens = 0;
        var pos = inputTokens;
        var budget = Math.Min(req.MaxNewTokens, Math.Max(0, maxLength - inputTokens));
        var generated = new List<int>(256);   // 反復ガード用：このターンの生成トークン列
        var generatedText = new StringBuilder(2048);
        var batch = new LLamaBatch();

        while (outputTokens < budget)
        {
            ct.ThrowIfCancellationRequested();
            var token = sampler.Sample(context.NativeHandle, lastLogit);
            prefillMs ??= sw.Elapsed.TotalMilliseconds;   // 初トークン到達時刻 ≒ prefill 所要
            sampler.Accept(token);

            // 終端トークン（Qwen3 は <|im_end|> 等）。KV へは載せない＝_fedTokens にも足さない。
            if (token.IsEndOfGeneration(weights.NativeHandle)) break;

            outputTokens++;
            var ti = (int)token;
            generated.Add(ti);

            var piece = StreamPiece(decoder, token);
            if (!string.IsNullOrEmpty(piece))
            {
                generatedText.Append(piece);
                sink.TryWrite(new TextDelta(piece));
            }

            // repetition collapse 保険（ONNX 版と同一基準）。ここで止める＝このトークンは KV へ載せない。
            if (DecodeLoopGuards.IsLoopingTail(generated) || DecodeLoopGuards.IsRepeatingTextTail(generatedText))
                break;

            // 次トークンのロジットを得るため、このトークンを KV へ載せる（decode）。
            batch.Clear();
            lastLogit = batch.Add(token, pos, LLamaSeqId.Zero, logits: true);
            if (context.Decode(batch) != DecodeResult.Ok) break;   // 窓溢れ等。停止する。
            _fedTokens.Add(ti);   // KV へ実際に載ったので prefix 計算へ反映
            pos++;
        }

        var totalGenMs = sw.Elapsed.TotalMilliseconds;
        var evalMs = prefillMs is { } p ? Math.Max(0, totalGenMs - p) : totalGenMs;
        sink.TryWrite(new AiUsageReported(
            inputTokens, outputTokens,
            loadMs > 0 ? loadMs : null, prefillMs, evalMs, loadMs + totalGenMs));
    }

    /// <summary>
    /// 今回プロンプトを KV に載せる。前回フィード分との最長共通接頭辞は再利用し、分岐分のみ
    /// チャンク分割して decode する。最終トークンは必ず（再）decode して logits を確定させ、その row index を返す
    /// （共通接頭辞がプロンプト全長と一致した場合でも末尾1トークンを decode し直すため、常にサンプリング可能）。
    /// </summary>
    private int Prefill(LLamaContext context, LLamaToken[] promptTokens, int[] promptInts, CancellationToken ct)
    {
        var n = promptTokens.Length;
        var common = TokenPrefix.CommonLength(_fedTokens, promptInts);
        var reuse = Math.Max(0, Math.Min(common, n - 1));   // 末尾1トークンは必ず decode し直す

        if (reuse < _fedTokens.Count)
        {
            context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, reuse, -1);   // [reuse, ∞) を破棄
            _fedTokens.RemoveRange(reuse, _fedTokens.Count - reuse);
        }

        var batch = new LLamaBatch();
        var lastLogit = -1;
        var inBatch = 0;
        for (var p = reuse; p < n; p++)
        {
            ct.ThrowIfCancellationRequested();
            var wantLogits = p == n - 1;
            var idx = batch.Add(promptTokens[p], p, LLamaSeqId.Zero, wantLogits);
            if (wantLogits) lastLogit = idx;
            _fedTokens.Add(promptInts[p]);
            inBatch++;

            if (inBatch >= PrefillChunkTokens || p == n - 1)
            {
                if (context.Decode(batch) != DecodeResult.Ok)
                    throw new InvalidOperationException(
                        "プロンプトの prefill に失敗しました（コンテキスト窓を超えた可能性があります）。");
                batch.Clear();
                inBatch = 0;
            }
        }
        return lastLogit;
    }

    private static string StreamPiece(StreamingTokenDecoder decoder, LLamaToken token)
    {
        decoder.Add(token);
        return decoder.Read();
    }

    /// <summary>サンプリング設定を LLamaSharp の <see cref="DefaultSamplingPipeline"/> へ写す。
    /// 未指定の項目はパイプライン既定に委ねる（Qwen3 プロファイルは temp/top_p/top_k/rep_penalty を全指定）。</summary>
    private static DefaultSamplingPipeline BuildSampler(SamplingOptions s)
    {
        // プロパティは init 専用。未指定の項目はパイプライン既定を引き継ぐため、既定値を一時インスタンスから
        // 読み、指定があるものだけ上書きしてオブジェクト初期化子で組み立てる。
        using var d = new DefaultSamplingPipeline();
        return new DefaultSamplingPipeline
        {
            Temperature = s.Temperature is { } t ? (float)t : d.Temperature,
            TopP = s.TopP is { } tp ? (float)tp : d.TopP,
            TopK = s.TopK is { } tk ? tk : d.TopK,
            RepeatPenalty = s.RepeatPenalty is { } rp ? (float)rp : d.RepeatPenalty,
        };
    }

    private void ResetContext()
    {
        _context?.Dispose();
        _context = null;
        _ctxSize = 0;
        _fedTokens.Clear();
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        InvalidOperationException or FileNotFoundException => ex.Message,
        _ => $"ローカル推論（llama.cpp）に失敗しました: {ex.Message}"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetContext();
        _weights?.Dispose();
        _gate.Dispose();
    }
}
