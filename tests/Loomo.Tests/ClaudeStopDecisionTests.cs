using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>「動きが悪くなったら Claude に直させる」（§45.6 P3）の判定。悪化が無ければ止め、あれば最大 2 回まで続けさせる。</summary>
public sealed class ClaudeStopDecisionTests
{
    [Fact]
    public void 悪化が無ければ止めてよい()
    {
        Assert.Null(ClaudeStopDecision.Block(null, 0));
        Assert.Null(ClaudeStopDecision.Block("   ", 0));
    }

    [Fact]
    public void 悪化があれば_block_を返し_理由に要約を入れる()
    {
        var reply = ClaudeStopDecision.Block("テスト：落ちた", 0);
        Assert.NotNull(reply);
        Assert.Equal("block", reply!["decision"]!.GetValue<string>());
        Assert.Contains("テスト：落ちた", reply["reason"]!.GetValue<string>());
    }

    [Fact]
    public void 同じ頼みごとで続けさせるのは最大_2_回()
    {
        Assert.NotNull(ClaudeStopDecision.Block("x", 0));
        Assert.NotNull(ClaudeStopDecision.Block("x", 1));
        Assert.Null(ClaudeStopDecision.Block("x", ClaudeStopDecision.MaxContinuationsPerPrompt));
    }

    [Fact]
    public void Stop_フックは待ち時間が長く_ほかは短いまま()
    {
        var settings = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        ClaudeHookInstaller.AddTo(settings, @"C:\x\sk0ya.Loomo.McpBridge.exe");
        var stop = settings["hooks"]!["Stop"]![0]!["hooks"]![0]!;
        var notification = settings["hooks"]!["Notification"]![0]!["hooks"]![0]!;
        Assert.Equal(ClaudeHookInstaller.StopTimeoutSeconds, stop["timeout"]!.GetValue<int>());
        Assert.Equal(ClaudeHookInstaller.DefaultTimeoutSeconds, notification["timeout"]!.GetValue<int>());
        Assert.Contains("Stop", ClaudeHookInstaller.Events);
    }
}
