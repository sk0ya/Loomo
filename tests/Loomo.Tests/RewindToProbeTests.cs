using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntimeGenAI;
using Xunit;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// /clear（会話の短縮）でのみ通る KV 切り詰め再利用パス（<c>RewindTo</c>）が、実際の ORT-GenAI で
/// 「生成を回した後の常駐 Generator に対して」機能するかを実機で直接確かめる probe。
/// 通常の連続ターンは常に延長（AppendTokens のみ）で RewindTo を踏まないため、この経路は未検証だった。
/// 手動実行用（モデル未配置なら skip）。
/// </summary>
public sealed class RewindToProbeTests
{
    private readonly ITestOutputHelper _out;
    public RewindToProbeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void RewindTo_after_generation_then_append_reuses_prefix()
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

        using var model = new Model(modelPath);
        using var tok = new Tokenizer(model);

        int[] Encode(string s) { using var seq = tok.Encode(s); return seq[0].ToArray(); }

        // 安定プレフィックス（system 相当）＋ターンA（長い）。A は prefix の純粋な延長になるよう連結する。
        var prefix = Encode("You are a helpful assistant operating a dev workspace. Tools: run_powershell, write_file, edit_file.");
        var turnA = Encode("You are a helpful assistant operating a dev workspace. Tools: run_powershell, write_file, edit_file. User: please enumerate every file and explain each in detail.");
        _out.WriteLine($"prefixLen={prefix.Length} turnALen={turnA.Length} isPrefix={turnA.Take(prefix.Length).SequenceEqual(prefix)}");

        using var gp = new GeneratorParams(model);
        gp.SetSearchOption("max_length", 2048);
        gp.SetSearchOption("do_sample", false);
        using var gen = new Generator(model, gp);

        // ターンA: prefill + 数トークン生成（実ターンの「停止」相当に途中まで回す）。
        gen.AppendTokens(turnA);
        for (var i = 0; i < 5 && !gen.IsDone(); i++) gen.GenerateNextToken();
        var afterGen = gen.TokenCount();
        _out.WriteLine($"after A gen: TokenCount={afterGen}");

        // /clear 相当: 安定プレフィックスまで切り詰めて、別ターンB（短い）を載せ直す。
        Exception? rewindEx = null;
        try { gen.RewindTo((ulong)prefix.Length); }
        catch (Exception ex) { rewindEx = ex; }
        _out.WriteLine(rewindEx is null
            ? $"RewindTo OK: TokenCount={gen.TokenCount()} (target={prefix.Length})"
            : $"RewindTo THREW: {rewindEx.GetType().Name}: {rewindEx.Message}");

        Assert.Null(rewindEx);
        Assert.Equal((ulong)prefix.Length, gen.TokenCount());

        // 切り詰め後に分岐分だけ追加 → 生成継続できるか。
        var turnB = Encode(" User: pwd");
        gen.AppendTokens(turnB);
        gen.GenerateNextToken();
        _out.WriteLine($"after B append+gen: TokenCount={gen.TokenCount()} (expect {prefix.Length + turnB.Length + 1})");
    }
}
