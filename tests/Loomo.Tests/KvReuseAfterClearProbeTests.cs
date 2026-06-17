using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 「停止 → /clear → 送信」をエンジンレベルで再現し、/clear 後の送信（短い・安定プレフィックス共有）の
/// prefill が小さい（=KV プレフィックス再利用が効く）か、大きい（=再 prefill されている）かを実測する probe。
/// 手動実行用（モデル未配置なら skip）。
/// </summary>
public sealed class KvReuseAfterClearProbeTests
{
    private readonly ITestOutputHelper _out;
    public KvReuseAfterClearProbeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Reuse_after_stop_then_clear()
    {
        if (Environment.GetEnvironmentVariable("RUN_AGENT_HARNESS") != "1")
            return; // 通常の dotnet test では走らせない（重い・モデル必須）

        var modelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "models", "phi-4-mini-instruct-cpu-int4");
        if (!File.Exists(Path.Combine(modelPath, "genai_config.json")))
        {
            _out.WriteLine($"skip: model not found at {modelPath}");
            return;
        }

        const string stable =
            "You are a helpful assistant operating a dev workspace. " +
            "Tools: run_powershell, write_file, edit_file. Answer in Japanese. " +
            "Workspace root: C:/Projects/Loomo. OS: Windows. Shell: PowerShell.";

        using var engine = new OnnxGenAiEngine();
        var sampling = SamplingOptions.Unspecified;

        async Task<(double? prefill, int outTok)> Run(string prompt, int maxNew, CancellationToken ct)
        {
            var ch = Channel.CreateUnbounded<AgentEvent>();
            var task = engine.GenerateAsync(
                new GenerationRequest(prompt, modelPath, 4096, maxNew, sampling), ch.Writer, ct);
            double? prefill = null; var outTok = 0;
            try
            {
                await foreach (var ev in ch.Reader.ReadAllAsync(ct))
                {
                    if (ev is AiUsageReported u) { prefill = u.PromptEvalMs; outTok = (int)(u.OutputTokens ?? 0); }
                }
            }
            catch (OperationCanceledException) { }
            await task;
            return (prefill, outTok);
        }

        // ウォームアップ相当：安定プレフィックスを prime（初回ロード＋prefill を済ませる）。
        await engine.PrimeAsync(modelPath, stable, 4096, sampling, default, s => _out.WriteLine($"prime: {s}"));

        // ターンA（長い）。途中で停止する。
        var promptA = stable + " <|user|>List every file in the repo and explain each one in great detail.<|end|><|assistant|>";
        using (var ctsA = new CancellationTokenSource())
        {
            var runA = Run(promptA, maxNew: 200, ctsA.Token);
            await Task.Delay(1500);           // 少し生成させてから停止
            ctsA.Cancel();
            var (pa, _) = await runA;
            _out.WriteLine($"A (stopped): prefill={pa:0}ms");
        }

        // /clear 相当：会話を捨てて、安定プレフィックス＋短い別ターンBを送る。
        var promptB = stable + " <|user|>pwd<|end|><|assistant|>";
        var (pb, ob) = await Run(promptB, maxNew: 40, default);
        _out.WriteLine($"B (after clear): prefill={pb:0}ms outTok={ob}");

        // 参考：再度同型の短いターンC（Bの後・延長ではなく再び切り詰め）。
        var promptC = stable + " <|user|>echo hi<|end|><|assistant|>";
        var (pc, oc) = await Run(promptC, maxNew: 40, default);
        _out.WriteLine($"C (after B): prefill={pc:0}ms outTok={oc}");

        Assert.True(true);
    }
}
