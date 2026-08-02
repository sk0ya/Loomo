using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public class DebugSessionOutcomeTests
{
    [Fact]
    public void Exception_stop_includes_name_and_message_in_one_normalized_heading()
    {
        var text = DebugSessionViewModel.NormalizeStopReason(
            new(null, 0, "exception", 1, "System.InvalidOperationException", "bad state"));
        Assert.Equal("例外停止: System.InvalidOperationException — bad state", text);
    }

    [Theory]
    [InlineData(0, null, false, true, DebugSessionEndKind.Normal, "正常終了")]
    [InlineData(7, null, false, true, DebugSessionEndKind.ExitCode, "終了コード 7")]
    [InlineData(null, null, true, true, DebugSessionEndKind.UserStopped, "ユーザー停止")]
    [InlineData(null, "adapter disconnected", false, true, DebugSessionEndKind.AdapterDisconnected, "adapter切断")]
    [InlineData(null, "launch error", false, false, DebugSessionEndKind.LaunchFailed, "起動失敗")]
    public void Classify_covers_the_five_roadmap_outcomes(int? code, string? reason,
        bool userStopped, bool reachedRunning, DebugSessionEndKind expectedKind, string expectedSummary)
    {
        var outcome = DebugSessionOutcome.Classify(code, reason, userStopped, reachedRunning);
        Assert.Equal(expectedKind, outcome.Kind);
        Assert.Equal(expectedSummary, outcome.Summary);
        Assert.False(string.IsNullOrWhiteSpace(outcome.NextAction));
    }
}
