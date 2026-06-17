using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntimeGenAI;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// ONNX Runtime GenAI を使った in-process ローカル推論エンジン（CPU 前提・モデル非依存：phi4-mini /
/// qwen3 など genai_config を持つ ONNX モデル共通）。多 GB の <see cref="Model"/>/<see cref="Tokenizer"/>
/// の寿命を所有し、初回ロードしたら常駐させる（コールドを初回 1 回に抑える）。生成は batch size 1 前提のため
/// <see cref="SemaphoreSlim"/> で直列化する。
///
/// 性能の要：<see cref="Generator"/> もセッション内で常駐させ、ターン間で前回フィードしたトークン列と
/// 今回プロンプトの<b>最長共通接頭辞は KV キャッシュを再利用</b>（<c>RewindTo</c> + 分岐分だけ
/// <c>AppendTokens</c>）する。CPU 実行では prefill が支配的なので、安定プレフィックス（system + tools +
/// 既存履歴）の再 prefill を初回 1 回に抑えるのが最大の効き手。生成結果は <see cref="TextDelta"/>
/// （逐次本文）＋<see cref="AiUsageReported"/>（自前計測の所要・トークン数）としてチャネルへ流す。
/// </summary>
public sealed class OnnxGenAiEngine : ILocalInferenceEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OgaHandle _oga = new();
    private Model? _model;
    private Tokenizer? _tokenizer;
    private string? _loadedPath;
    private bool _disposed;

    // 常駐 Generator とその KV に現在入っているトークン列（プロンプト＋生成済み）。ターン間で再利用する。
    private Generator? _generator;
    private readonly List<int> _fedTokens = new();
    private int _generatorMaxLength;
    private SamplingOptions? _generatorSampling;

    // prefill（プロンプト評価）を一括ではなくこの粒度で AppendTokens し、チャンク間で停止要求を確認する。
    // CPU では prefill が支配的かつ AppendTokens は同期ブロッキングで途中中断できないため、分割しないと
    // prefill 中の停止が効かない（完走まで無反応になる）。粒度を小さくすると応答性は上がるが prefill が
    // 細切れになるオーバーヘッドが増えるので、停止の体感（数百ms以内）と両立する値にする。
    private const int PrefillChunkTokens = 128;

    /// <summary>モデルがロード済みか。</summary>
    public bool IsLoaded => _model is not null;

    /// <summary>モデルを事前ロードする（ウォームアップ用）。失敗時は例外を投げる。</summary>
    public async Task EnsureLoadedAsync(string modelPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { LoadIfNeeded(modelPath); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 常駐 <see cref="_generator"/> の KV を、<b>最初の実ターンとバイト単位で一致する安定プレフィックス</b>
    /// （system プロンプト＋ツール定義）で前もって満たしておく（暖機）。狙いは2つ:
    /// <list type="bullet">
    ///   <item><see cref="Generator.AppendTokens"/> 内の prefill が全レイヤーの重みに触れ、mmap した数 GB を
    ///   ディスク→RAM へページインさせる（<c>new Model()</c> だけでは触れず、初回プロンプトで払う羽目になる）。</item>
    ///   <item>初回の実ターンは <see cref="PrepareKvCache"/> の最長共通接頭辞でこの KV を<b>再利用</b>するため、
    ///   安定プレフィックスの prefill を払い直さずに済む（CPU 実行では prefill が支配的なので効果大）。</item>
    /// </list>
    /// 必ず実ターンと同じ <paramref name="maxLength"/>／<paramref name="sampling"/> で呼ぶこと
    /// （不一致だと <see cref="EnsureGenerator"/> が Generator を作り直し、温めた KV が消える）。
    /// 起動時の root は未確定なので、ワークスペース確定後に同じプレフィックスで呼び直せば再利用が最大化する
    /// （差分だけ <see cref="PrepareKvCache"/> が貼り直す）。
    /// </summary>
    public async Task PrimeAsync(
        string modelPath,
        string stablePrompt,
        int maxLength,
        SamplingOptions sampling,
        CancellationToken ct,
        Action<string>? progress = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // モデルロード（数GB）と prefill は CPU 同期処理。_gate は非占有時に同期完了するため、
            // Task.Run へ逃がさないと呼び出し元（起動時は UI スレッド）をブロックしてしまう。
            await Task.Run(() =>
            {
                progress?.Invoke("モデルをロードしています");
                var loadedNow = LoadIfNeeded(modelPath);
                progress?.Invoke(loadedNow
                    ? "プロンプトをトークン化しています"
                    : "プロンプトをトークン化しています（モデルはロード済み）");

                int[] tokens;
                using (var seq = _tokenizer!.Encode(stablePrompt))
                    tokens = seq[0].ToArray();

                progress?.Invoke($"生成器を準備しています（{tokens.Length} トークン）");
                EnsureGenerator(_model!, maxLength, sampling);
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"KVキャッシュを作成しています（prefill {tokens.Length} トークン）");
                PrepareKvCache(_generator!, tokens, ct);   // prefill（重みのページイン）＋ _fedTokens を記録
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
            var loadedNow = LoadIfNeeded(request.ModelPath);
            var loadMs = loadedNow ? loadSw.Elapsed.TotalMilliseconds : 0;
            var model = _model!;
            var tokenizer = _tokenizer!;
            // 生成は同期ブロッキング（CPU）。UI/呼び出し側をブロックしないようバックグラウンドで回す。
            await Task.Run(() => RunSync(model, tokenizer, request, loadMs, sink, ct), ct);
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

    /// <summary>必要ならモデルを（再）ロードする。実際にロードしたら true。呼び出しは _gate 内で行うこと。</summary>
    private bool LoadIfNeeded(string modelPath)
    {
        if (_model is not null && string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase))
            return false;

        ValidateModelPath(modelPath);

        ResetGenerator();
        _tokenizer?.Dispose();
        _model?.Dispose();
        _model = null; _tokenizer = null; _loadedPath = null;

        var model = new Model(modelPath);
        _tokenizer = new Tokenizer(model);
        _model = model;
        _loadedPath = modelPath;
        return true;
    }

    private static void ValidateModelPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new InvalidOperationException(
                "モデルフォルダが設定されていません。設定で ONNX モデルのフォルダを指定するか、ダウンロードしてください。");
        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"モデルフォルダが見つかりません: {modelPath}");
        if (!File.Exists(Path.Combine(modelPath, "genai_config.json")))
            throw new FileNotFoundException(
                $"genai_config.json が見つかりません（ONNX モデルフォルダではありません）: {modelPath}");
    }

    private void RunSync(
        Model model, Tokenizer tokenizer, GenerationRequest req, double loadMs,
        ChannelWriter<AgentEvent> sink, CancellationToken ct)
    {
        int[] promptTokens;
        using (var sequences = tokenizer.Encode(req.Prompt))
            promptTokens = sequences[0].ToArray();
        var inputTokens = promptTokens.Length;

        var maxLength = req.MaxLength > 0 ? req.MaxLength : 4096;
        EnsureGenerator(model, maxLength, req.Sampling);
        var generator = _generator!;

        using var stream = tokenizer.CreateStream();

        // prefill（プロンプト評価）は AppendTokens 内で走る。AppendTokens の前から計測し、
        // 「最初のトークンが出るまで」を prefill とみなす（計測漏れを防ぐ）。
        var sw = Stopwatch.StartNew();
        PrepareKvCache(generator, promptTokens, ct);

        double? prefillMs = null;
        var outputTokens = 0;
        var budget = Math.Min(req.MaxNewTokens, Math.Max(0, maxLength - inputTokens));
        var generated = new List<int>(256);    // 反復ガード用：このターンの生成トークン列
        var generatedText = new StringBuilder(2048);

        while (outputTokens < budget && !generator.IsDone())
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
            prefillMs ??= sw.Elapsed.TotalMilliseconds;

            var seq = generator.GetSequence(0);
            var token = seq[seq.Length - 1];
            _fedTokens.Add(token);                 // 生成トークンも次ターンの共通接頭辞計算に含める
            generated.Add(token);
            outputTokens++;

            var piece = stream.Decode(token);
            if (!string.IsNullOrEmpty(piece))
            {
                generatedText.Append(piece);
                sink.TryWrite(new TextDelta(piece));
            }

            // repetition collapse 保険。短いトークン周期だけでなく、同じ文章/JSONブロックを何度も
            // 生成する長周期ループも止める（ORT の no_repeat_ngram_size は 0.9.0 CPU で無視された。
            // 0.14.1 でも挙動は未再確認のため、この決定論的ガードを常に効かせて確実に停止させる）。
            if (IsLoopingTail(generated) || IsRepeatingTextTail(generatedText))
                break;
        }

        var totalGenMs = sw.Elapsed.TotalMilliseconds;
        var evalMs = prefillMs is { } p ? Math.Max(0, totalGenMs - p) : totalGenMs;  // decode 時間
        sink.TryWrite(new AiUsageReported(
            inputTokens, outputTokens,
            loadMs > 0 ? loadMs : null, prefillMs, evalMs, loadMs + totalGenMs));
    }

    /// <summary>
    /// 末尾が短周期の繰り返しループに陥っているか（repetition collapse の検知）。長さ <paramref name="maxUnit"/>
    /// 以下の繰り返し単位が末尾で <paramref name="minRepeats"/> 回以上連続していれば true。" . " のような
    /// 1〜数トークンの暴走を捕まえる。エージェント／ツール用途では短周期の多数回反復はまず崩壊なので、
    /// 正常な短い反復を巻き込まないよう繰り返し回数のしきい値は高めに取る。
    /// </summary>
    private static bool IsLoopingTail(List<int> g, int maxUnit = 8, int minRepeats = 10)
    {
        for (var unit = 1; unit <= maxUnit; unit++)
        {
            var need = unit * minRepeats;
            if (g.Count < need) continue;

            var looping = true;
            for (var k = 1; k < minRepeats && looping; k++)
                for (var j = 0; j < unit; j++)
                    if (g[g.Count - 1 - j] != g[g.Count - 1 - j - k * unit]) { looping = false; break; }

            if (looping) return true;
        }
        return false;
    }

    /// <summary>
    /// デコード済みテキスト末尾で、同じ文章ブロックが連続しているかを検出する。
    /// トークン単位では捕まえにくい、数十〜数百文字の回答ブロック反復を止めるための保険。
    /// </summary>
    internal static bool IsRepeatingTextTail(StringBuilder text, int minUnitChars = 24, int maxUnitChars = 600, int minRepeats = 3)
    {
        var len = text.Length;
        if (len < minUnitChars * minRepeats) return false;

        var maxUnit = Math.Min(maxUnitChars, len / minRepeats);
        for (var unit = minUnitChars; unit <= maxUnit; unit++)
        {
            var repeated = true;
            for (var r = 1; r < minRepeats && repeated; r++)
            {
                var a = len - unit;
                var b = len - unit * (r + 1);
                for (var i = 0; i < unit; i++)
                {
                    if (text[a + i] == text[b + i]) continue;
                    repeated = false;
                    break;
                }
            }

            if (repeated) return true;
        }

        return false;
    }

    /// <summary>
    /// 今回プロンプトを Generator の KV に載せる。前回フィード分との最長共通接頭辞は再利用し、分岐分のみ
    /// <c>AppendTokens</c> で再 prefill する。分岐分は <see cref="PrefillChunkTokens"/> 単位で分割して載せ、
    /// チャンク間で <paramref name="ct"/> を確認して停止に応答する（一括だと prefill 完走まで停止が効かない）。
    /// 失敗時は Generator を作り直して全プロンプトを載せ直す（保険）。停止要求はそのまま伝播させる。
    /// </summary>
    private void PrepareKvCache(Generator generator, int[] promptTokens, CancellationToken ct)
    {
        try
        {
            var common = TokenPrefix.CommonLength(_fedTokens, promptTokens);

            if (common < _fedTokens.Count)
            {
                generator.RewindTo((ulong)common);             // 共通接頭辞より後ろの KV を捨てる
                _fedTokens.RemoveRange(common, _fedTokens.Count - common);
            }

            // 分岐分をチャンクに分けて prefill。途中で停止しても _fedTokens は実際に KV へ載ったぶんだけ
            // 進めるので、次ターンの最長共通接頭辞計算（prefix 再利用）が壊れない。
            for (var pos = common; pos < promptTokens.Length; pos += PrefillChunkTokens)
            {
                ct.ThrowIfCancellationRequested();
                var end = Math.Min(pos + PrefillChunkTokens, promptTokens.Length);
                generator.AppendTokens(promptTokens.AsSpan(pos, end - pos));
                for (var i = pos; i < end; i++)
                    _fedTokens.Add(promptTokens[i]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;     // 停止要求は prefill 失敗扱いにせず伝播（再作成パスへ落とさない）
        }
        catch
        {
            // 再利用パスで失敗したら作り直して全量を載せ直す（このターンは prefix 再利用の恩恵なし）。
            RecreateGeneratorAndFeed(promptTokens);
        }
    }

    /// <summary>Generator が無い／max_length・サンプリングが変わった場合に作り直す（KV はクリア）。</summary>
    private void EnsureGenerator(Model model, int maxLength, SamplingOptions sampling)
    {
        if (_generator is not null && _generatorMaxLength == maxLength && _generatorSampling == sampling)
            return;
        BuildGenerator(model, maxLength, sampling);
    }

    private void BuildGenerator(Model model, int maxLength, SamplingOptions sampling)
    {
        ResetGenerator();
        using var gp = new GeneratorParams(model);
        gp.SetSearchOption("max_length", maxLength);
        ApplySampling(gp, sampling);
        _generator = new Generator(model, gp);
        _generatorMaxLength = maxLength;
        _generatorSampling = sampling;
    }

    private void RecreateGeneratorAndFeed(int[] promptTokens)
    {
        // model / maxLength / sampling は直近のものを引き継ぐ。
        BuildGenerator(_model!, _generatorMaxLength, _generatorSampling ?? SamplingOptions.Unspecified);
        if (promptTokens.Length > 0)
        {
            _generator!.AppendTokens(promptTokens.AsSpan());
            _fedTokens.AddRange(promptTokens);
        }
    }

    private void ResetGenerator()
    {
        _generator?.Dispose();
        _generator = null;
        _fedTokens.Clear();
        _generatorMaxLength = 0;
        _generatorSampling = null;
    }

    /// <summary>サンプリング設定を ORT-GenAI の search option へ写す。1つでも指定があれば do_sample を有効化する。</summary>
    private static void ApplySampling(GeneratorParams gp, SamplingOptions s)
    {
        var sampled = false;
        if (s.Temperature is { } t) { gp.SetSearchOption("temperature", t); sampled = true; }
        if (s.TopP is { } p) { gp.SetSearchOption("top_p", p); sampled = true; }
        if (s.TopK is { } k) { gp.SetSearchOption("top_k", k); sampled = true; }
        if (s.RepeatPenalty is { } r) gp.SetSearchOption("repetition_penalty", r);
        gp.SetSearchOption("do_sample", sampled);
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        InvalidOperationException or DirectoryNotFoundException or FileNotFoundException => ex.Message,
        _ => $"ローカル推論に失敗しました: {ex.Message}"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetGenerator();
        _tokenizer?.Dispose();
        _model?.Dispose();
        _oga.Dispose();
        _gate.Dispose();
    }
}
