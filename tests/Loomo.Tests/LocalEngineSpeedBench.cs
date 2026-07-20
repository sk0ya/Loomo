using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ローカル推論エンジンの素の速度（prefill / decode の tok/s）を、エージェントループや
/// ツール反復のばらつきを排して直接計測するマイクロベンチ。固定プロンプトを1回 <see cref="ILocalInferenceEngine"/>
/// へ流し、<see cref="AiUsageReported"/> のトークン数と所要から tok/s を出す。ONNX / llama.cpp を
/// 同条件（同じ Qwen3 プロンプト・同じ出力予算・CPU）で比較するための「速度だけ」の物差し。
/// 既定では Skip（手動実行）。RUN_SPEED_BENCH=1 で有効化。HARNESS_MODEL でモデル切替（ハーネスと同じ）。
/// </summary>
public sealed class LocalEngineSpeedBench
{
    private readonly ITestOutputHelper _out;
    public LocalEngineSpeedBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task BenchPrefillAndDecode()
    {
        if (Environment.GetEnvironmentVariable("RUN_SPEED_BENCH") != "1")
            return;

        var name = Environment.GetEnvironmentVariable("HARNESS_MODEL") is { Length: > 0 } m
            ? m : AiSettings.DefaultLocalModel;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "models", name);
        // ONNX はフォルダ、GGUF はフォルダ内の .gguf ファイルを指す（ルータが拡張子で振り分ける）。
        var modelPath = Directory.Exists(root) && !File.Exists(Path.Combine(root, "genai_config.json"))
            ? Directory.EnumerateFiles(root, "*.gguf").OrderBy(p => p).FirstOrDefault() ?? root
            : root;
        Assert.True(Directory.Exists(modelPath) || File.Exists(modelPath), $"model not found: {modelPath}");

        var settings = new AiSettings();
        settings.Local.Model = name;
        settings.Local.ModelPath = modelPath;

        var profile = ModelProfiles.Resolve(name);
        var maxLength = ModelProfiles.EffectiveNumCtx(name, settings.Local.NumCtx);

        // 文章生成を十分に走らせて decode tok/s を安定計測するため、長めの回答を促す。ツールは含めない
        // （decode 速度は prompt 内容に依存しない。prefill tok/s も throughput なので長さ非依存）。
        var convo = new Conversation();
        convo.AddUser("ローカルでLLMを動かす利点と注意点を、日本語の文章で400字程度、詳しく説明してください。箇条書きは使わないでください。");
        var prompt = ChatPrompt.Build(profile.Format, settings, AgentProfiles.Root, new[] { @"C:\Projects\Loomo" },
            convo, Array.Empty<ToolDefinition>());

        const int budget = 300;
        var request = new GenerationRequest(prompt, modelPath, maxLength, budget, profile.Sampling);

        using var engine = new LocalInferenceRouter(new OnnxGenAiEngine(), new LlamaCppEngine());

        // 2回計測する：1回目は load＋cold prefill、2回目は同一プロンプトで KV プレフィックスを再利用した
        // 「温まった」状態。実運用（暖機後・履歴継続）に近い 2回目を tok/s の代表値とする。
        AiUsageReported? warm = null;
        for (var pass = 0; pass < 2; pass++)
        {
            var channel = Channel.CreateUnbounded<AgentEvent>();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var gen = engine.GenerateAsync(request, channel.Writer, cts.Token);
            AiUsageReported? usage = null;
            await foreach (var ev in channel.Reader.ReadAllAsync(cts.Token))
                if (ev is AiUsageReported u) usage = u;
            await gen;

            if (usage is null) { _out.WriteLine($"[{name}] pass {pass}: no usage reported"); continue; }
            warm = usage;
            var inTok = usage.InputTokens ?? 0;
            var outTok = usage.OutputTokens ?? 0;
            var pre = usage.PromptEvalMs ?? 0;
            var dec = usage.EvalMs ?? 0;
            var preTps = pre > 0 ? inTok / (pre / 1000.0) : 0;
            var decTps = dec > 0 ? outTok / (dec / 1000.0) : 0;
            _out.WriteLine(
                $"[{name}] pass {pass} ({(pass == 0 ? "cold" : "warm")}): " +
                $"in={inTok} out={outTok} load={usage.LoadMs ?? 0:F0}ms " +
                $"prefill={pre:F0}ms ({preTps:F1} tok/s) decode={dec:F0}ms ({decTps:F1} tok/s)");
        }

        // 代表値（warm パス）を1行サマリとして専用ログへ追記。3モデル分を後で1枚に集約する。
        if (warm is not null)
        {
            var inTok = warm.InputTokens ?? 0;
            var outTok = warm.OutputTokens ?? 0;
            var preTps = (warm.PromptEvalMs ?? 0) > 0 ? inTok / ((warm.PromptEvalMs ?? 0) / 1000.0) : 0;
            var decTps = (warm.EvalMs ?? 0) > 0 ? outTok / ((warm.EvalMs ?? 0) / 1000.0) : 0;
            var dir = @"C:\Projects\Loomo\docs\reports";
            try { Directory.CreateDirectory(dir); } catch { }
            try
            {
                File.AppendAllText(Path.Combine(dir, "speed-bench.log"),
                    $"{name}\tin={inTok}\tout={outTok}\tprefill_tps={preTps:F1}\tdecode_tps={decTps:F1}" +
                    $"\tprefill_ms={warm.PromptEvalMs ?? 0:F0}\tdecode_ms={warm.EvalMs ?? 0:F0}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
