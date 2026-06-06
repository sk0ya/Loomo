using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntimeGenAI;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// ONNX Runtime GenAI を使った in-process ローカル推論エンジン（CPU・phi4-mini 前提）。
/// 多 GB の <see cref="Model"/>/<see cref="Tokenizer"/> の寿命を所有し、初回ロードしたら常駐させる
/// （コールドを初回 1 回に抑える）。生成は batch size 1 前提のため <see cref="SemaphoreSlim"/> で直列化する。
///
/// 性能の要：<see cref="Generator"/> もセッション内で常駐させ、ターン間で前回フィードしたトークン列と
/// 今回プロンプトの<b>最長共通接頭辞は KV キャッシュを再利用</b>（<c>RewindTo</c> + 分岐分だけ
/// <c>AppendTokens</c>）する。CPU 実行では prefill が支配的なので、安定プレフィックス（system + tools +
/// 既存履歴）の再 prefill を初回 1 回に抑えるのが最大の効き手。生成結果は <see cref="TextDelta"/>
/// （逐次本文）＋<see cref="AiUsageReported"/>（自前計測の所要・トークン数）としてチャネルへ流す。
/// </summary>
public sealed class Phi4Engine : ILocalInferenceEngine, IDisposable
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
    /// モデルをロードし、さらに<b>ダミー生成を1回</b>走らせて重みをページインさせる（暖機）。
    /// <c>new Model()</c> だけでは mmap した数 GB の重みに触れず、最初の実プロンプトの prefill で
    /// 初回タッチのページイン（ディスク→RAM、CPU 実行で十数秒）を払ってしまう。それを起動時の
    /// バックグラウンドへ前倒しして、ユーザーの初回プロンプトを暖かい状態にする。
    /// </summary>
    public async Task WarmUpAsync(string modelPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            LoadIfNeeded(modelPath);
            RunWarmup(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>ダミーの短いプロンプトで prefill＋1トークン生成を走らせ、全レイヤーの重みをページインさせる。
    /// 常駐 <see cref="_generator"/> は使わず使い捨ての Generator で行うので、本番の prefix 再利用を汚さない。</summary>
    private void RunWarmup(CancellationToken ct)
    {
        var model = _model!;
        var tokenizer = _tokenizer!;
        int[] tokens;
        using (var seq = tokenizer.Encode("warm up"))
            tokens = seq[0].ToArray();

        using var gp = new GeneratorParams(model);
        gp.SetSearchOption("max_length", tokens.Length + 1);
        gp.SetSearchOption("do_sample", false);
        using var gen = new Generator(model, gp);
        gen.AppendTokens(tokens.AsSpan());
        ct.ThrowIfCancellationRequested();
        if (!gen.IsDone())
            gen.GenerateNextToken();   // ここで全重みに触れてページインさせる
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
        PrepareKvCache(generator, promptTokens);

        double? prefillMs = null;
        var outputTokens = 0;
        var budget = Math.Min(req.MaxNewTokens, Math.Max(0, maxLength - inputTokens));

        while (outputTokens < budget && !generator.IsDone())
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
            prefillMs ??= sw.Elapsed.TotalMilliseconds;

            var seq = generator.GetSequence(0);
            var token = seq[seq.Length - 1];
            _fedTokens.Add(token);                 // 生成トークンも次ターンの共通接頭辞計算に含める
            outputTokens++;

            var piece = stream.Decode(token);
            if (!string.IsNullOrEmpty(piece))
                sink.TryWrite(new TextDelta(piece));
        }

        var totalGenMs = sw.Elapsed.TotalMilliseconds;
        var evalMs = prefillMs is { } p ? Math.Max(0, totalGenMs - p) : totalGenMs;  // decode 時間
        sink.TryWrite(new AiUsageReported(
            inputTokens, outputTokens,
            loadMs > 0 ? loadMs : null, prefillMs, evalMs, loadMs + totalGenMs));
    }

    /// <summary>
    /// 今回プロンプトを Generator の KV に載せる。前回フィード分との最長共通接頭辞は再利用し、分岐分のみ
    /// <c>AppendTokens</c> で再 prefill する。失敗時は Generator を作り直して全プロンプトを載せ直す（保険）。
    /// </summary>
    private void PrepareKvCache(Generator generator, int[] promptTokens)
    {
        try
        {
            var common = TokenPrefix.CommonLength(_fedTokens, promptTokens);

            if (common < _fedTokens.Count)
            {
                generator.RewindTo((ulong)common);             // 共通接頭辞より後ろの KV を捨てる
                _fedTokens.RemoveRange(common, _fedTokens.Count - common);
            }

            if (common < promptTokens.Length)
            {
                var suffix = promptTokens.AsSpan(common);
                generator.AppendTokens(suffix);                // 分岐分だけ再 prefill
                for (var i = common; i < promptTokens.Length; i++)
                    _fedTokens.Add(promptTokens[i]);
            }
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
