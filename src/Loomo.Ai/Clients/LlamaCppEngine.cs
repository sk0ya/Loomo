using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
///
/// この再利用は<b>純粋な Attention Transformer 限定</b>。Mamba/SSM 系の再帰メモリを持つハイブリッド
/// アーキテクチャ（例: Qwen3.5 の "qwen35"、Gated Delta Net）では再帰状態を途中の位置まで巻き戻せない
/// ため、GGUF メタデータの <c>*.ssm.*</c> キーの有無で検出して常に全破棄→完全再 prefill に切り替える
/// （<see cref="_isRecurrent"/> 参照）。<see cref="SafeLlamaModelHandle.IsRecurrent"/>（<c>llama_model_is_recurrent</c>）
/// は実機確認したところ Qwen3.5 のような<b>ハイブリッド</b>では false を返す（純粋 Mamba/RWKV 等の
/// 判定用らしく、ハイブリッドは対象外）ため、判定には使えない。
/// </summary>
public sealed class LlamaCppEngine : ILocalInferenceEngine, ILocalWarmableEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private NativeModelPath? _nativeModelPath;
    private string? _loadedPath;
    private int _ctxSize;
    private bool _disposed;

    // KV に現在入っているトークン列（プロンプト＋生成済み）。ターン間で最長共通接頭辞の再利用に使う。
    private readonly List<int> _fedTokens = new();

    // Mamba/SSM 系の再帰メモリ（"Gated Delta Net" 等）を持つハイブリッドアーキテクチャか（例: Qwen3.5 の qwen35）。
    // 通常の Attention KV は位置指定で部分破棄できるが、再帰状態は「途中の位置まで巻き戻す」ことができない
    // （RNN の隠れ状態と同じで、逐次上書きされるのみ）。このため MemorySequenceRemove(reuse>0, -1) による
    // 部分再利用は、Attention KV は正しく巻き戻っても再帰状態は前ターン終端のまま残り、次の Decode が
    // 即座に失敗する（実機で Qwen3.5-9B GGUF にて確認。しかも MemoryClear(true) による全クリアに変えても
    // 再現した＝reuse=0 側の呼び出しでも直らない）。
    private bool _isRecurrent;

    // 再帰メモリ搭載モデル向けの代替キャッシュ：位置ベースの部分巻き戻しの代わりに、PrimeAsync で確定した
    // 安定プレフィックス（system+tools）の直後の状態を GetState/LoadState で丸ごとスナップショット・復元する
    // （llama_state_seq_get_data/set_data は KV も再帰状態もまるごと保存/復元する不透明データなので、位置
    // 指定の部分除去と違い再帰メモリでも正しく機能する）。プロンプト先頭がこのトークン列と一致する間だけ
    // 有効で、不一致・未取得なら Prefill が MemoryClear による全破棄→完全再 prefill にフォールバックする。
    private LLamaContext.SequenceState? _checkpoint;
    private int[]? _checkpointTokens;

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
                if (_isRecurrent)
                {
                    // ここで確定した安定プレフィックス（system+tools）の直後状態を1つだけスナップショットし、
                    // 以降の実ターンでプロンプト先頭がこれと一致すれば復元して再利用する。
                    _checkpoint?.Dispose();
                    _checkpoint = context.GetState(LLamaSeqId.Zero);
                    _checkpointTokens = (int[])promptInts.Clone();
                }
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
            _nativeModelPath?.Dispose();
            _weights = null; _nativeModelPath = null; _loadedPath = null;

            var nativeModelPath = NativeModelPath.Create(modelPath);
            LLamaWeights? weights = null;
            LLamaContext? context = null;
            try
            {
                var mp = MakeParams(nativeModelPath.Path, maxLength);
                weights = LLamaWeights.LoadFromFile(mp);
                context = weights.CreateContext(mp);
                // NativeHandle.IsRecurrent は「純粋な」再帰アーキテクチャ判定用らしく、Qwen3.5 のような
                // Attention+SSM ハイブリッドでは実機確認で false を返したため使えない。GGUF メタデータの
                // "<arch>.ssm.*" キーの有無で汎用的に検出する（アーキテクチャ名を個別に列挙しない）。
                _isRecurrent = weights.NativeHandle.IsRecurrent
                    || weights.Metadata.Keys.Any(k => k.Contains(".ssm.", StringComparison.Ordinal));
            }
            catch
            {
                context?.Dispose();
                weights?.Dispose();
                nativeModelPath.Dispose();
                throw;
            }

            _nativeModelPath = nativeModelPath;
            _weights = weights;
            _loadedPath = modelPath;
            _context = context;
            _ctxSize = maxLength;
            _fedTokens.Clear();
            return true;
        }

        // 重みは同一。コンテキストが無い／窓が小さすぎるなら作り直す（KV はクリア）。
        if (_context is null || _ctxSize < maxLength)
        {
            ResetContext();
            _context = _weights.CreateContext(MakeParams(_nativeModelPath!.Path, maxLength));
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
        int reuse;

        if (_isRecurrent)
        {
            if (_fedTokens.Count > 0 && n > _fedTokens.Count
                && TokenPrefix.CommonLength(_fedTokens, promptInts) == _fedTokens.Count)
            {
                // 今 KV／再帰状態に載っている内容がそのまま新プロンプトの厳密な接頭辞（同一会話の続き）。
                // 巻き戻し不要＝何も削除・復元せず、続きを追記するだけでよい最安経路
                // （MemorySequenceRemove も LoadState も使わないため再帰メモリでも無条件に安全）。
                reuse = _fedTokens.Count;
            }
            else if (_checkpoint is not null && _checkpointTokens is not null && n > _checkpointTokens.Length
                && TokenPrefix.CommonLength(_checkpointTokens, promptInts) == _checkpointTokens.Length)
            {
                // プロンプト先頭が安定プレフィックスと一致：スナップショットを復元し、続きだけ prefill する。
                context.LoadState(_checkpoint, LLamaSeqId.Zero);
                _fedTokens.Clear();
                _fedTokens.AddRange(_checkpointTokens);
                reuse = _checkpointTokens.Length;
            }
            else
            {
                // 不一致・チェックポイント未取得のフォールバック。既知の参照トークン列（チェックポイント、
                // 無ければ現在 KV に載っている分＝直前の PrimeAsync/ターンの内容）との共通接頭辞を実測し、
                // その境界までを独立して prefill し直してチェックポイントを取り直す。
                // （PrimeAsync が渡す stablePrompt は「会話が空」の状態で組んだ文字列なので、生成開始マーカ
                // が system ブロック直後に付き、実ターン（system→ユーザー発話→生成開始マーカ）とは末尾が
                // 食い違う＝バイト完全一致しない。実測した共通接頭辞＝system+tools の真の境界を使うことで、
                // 1回だけこの発見コストを払えば以降のターンは正しい境界にヒットして高速化される。）
                // 実機検証: MemorySequenceRemove は reuse=0（全除去相当）で呼んでも再帰状態が残り、
                // 次ターンの Decode が即座に失敗した（seq 位置ベースの除去は再帰メモリに正しく伝播しない）。
                // 位置指定に依らずメモリ全体を明示的にクリアする MemoryClear を使う。
                var reference = _checkpointTokens ?? (_fedTokens.Count > 0 ? _fedTokens.ToArray() : null);
                var boundary = reference is null ? 0 : Math.Min(TokenPrefix.CommonLength(reference, promptInts), n - 1);

                if (_fedTokens.Count > 0)
                    context.NativeHandle.MemoryClear(true);
                _fedTokens.Clear();

                if (boundary > 0)
                {
                    FeedRange(context, promptTokens, promptInts, 0, boundary, wantLastLogit: false, ct);
                    _checkpoint?.Dispose();
                    _checkpoint = context.GetState(LLamaSeqId.Zero);
                    _checkpointTokens = promptInts[..boundary];
                }
                reuse = boundary;
            }
        }
        else
        {
            var common = TokenPrefix.CommonLength(_fedTokens, promptInts);
            reuse = Math.Max(0, Math.Min(common, n - 1));   // 末尾1トークンは必ず decode し直す
            if (reuse < _fedTokens.Count)
            {
                context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, reuse, -1);   // [reuse, ∞) を破棄
                _fedTokens.RemoveRange(reuse, _fedTokens.Count - reuse);
            }
        }

        reuse = Math.Max(0, Math.Min(reuse, n - 1));   // 末尾1トークンは必ず decode し直す（両経路共通の安全弁）
        return FeedRange(context, promptTokens, promptInts, reuse, n, wantLastLogit: true, ct);
    }

    /// <summary>[start, end) を _fedTokens 追記込みでチャンク分割 decode する。<paramref name="wantLastLogit"/>
    /// が true のときだけ末尾トークンの logits を要求し、その row index を返す（false なら -1）。</summary>
    private int FeedRange(
        LLamaContext context, LLamaToken[] promptTokens, int[] promptInts, int start, int end,
        bool wantLastLogit, CancellationToken ct)
    {
        var batch = new LLamaBatch();
        var lastLogit = -1;
        var inBatch = 0;
        for (var p = start; p < end; p++)
        {
            ct.ThrowIfCancellationRequested();
            var wantLogits = wantLastLogit && p == end - 1;
            var idx = batch.Add(promptTokens[p], p, LLamaSeqId.Zero, wantLogits);
            if (wantLogits) lastLogit = idx;
            _fedTokens.Add(promptInts[p]);
            inBatch++;

            if (inBatch >= PrefillChunkTokens || p == end - 1)
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
        _checkpoint?.Dispose();
        _checkpoint = null;
        _checkpointTokens = null;
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
        _nativeModelPath?.Dispose();
        _gate.Dispose();
    }
}
