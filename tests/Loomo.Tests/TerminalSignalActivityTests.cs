using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ターミナルのコマンドが出す進捗（OSC 9;4）とベルを袖のバッジへ（§24.35）。</summary>
public sealed class TerminalSignalActivityTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void 進捗は実行中より優先し_いちばん遅れているタブの値を出す()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(A, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalCommandChanged(B, commandExecuted: true, commandDone: false, null, paneWatched: false);
        Assert.True(sut.TerminalProgressChanged(A, new(TerminalProgressKind.Normal, 80)));
        Assert.True(sut.TerminalProgressChanged(B, new(TerminalProgressKind.Normal, 30)));

        Assert.Equal(PaneActivityKind.Progress, sut.AggregateTerminal(out _, out var progress));
        Assert.Equal(30, progress.Percent);
        Assert.Equal("● 30%", PaneActivityPresentation.ProgressText(progress));
    }

    [Fact]
    public void 同じ進捗なら描き直さず_エラーの進捗は赤のバッジになる()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalProgressChanged(A, new(TerminalProgressKind.Normal, 10));
        Assert.False(sut.TerminalProgressChanged(A, new(TerminalProgressKind.Normal, 10)));

        sut.TerminalProgressChanged(A, new(TerminalProgressKind.Error, 40));

        var activity = sut.AggregateTerminal(out _, out var progress);
        Assert.Equal(PaneActivityKind.ProgressError, activity);
        Assert.Equal(PaneActivityBadgeTone.Failed, PaneActivityPresentation.BadgeTone(activity));
        Assert.Equal("✗ 40%", PaneActivityPresentation.ProgressText(progress));
    }

    [Fact]
    public void コマンドが終われば進捗を0に戻さなくても消える()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(A, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalProgressChanged(A, new(TerminalProgressKind.Indeterminate, 0));
        Assert.Equal(PaneActivityKind.Progress, sut.AggregateTerminal(out _, out _));

        sut.TerminalCommandChanged(A, commandExecuted: false, commandDone: true, 0, paneWatched: false);

        Assert.Equal(PaneActivityKind.Succeeded, sut.AggregateTerminal(out _, out _));
    }

    [Fact]
    public void 見ていない間のベルは残り_ペインを見ると消える()
    {
        var sut = new PaneActivityTracker();
        Assert.False(sut.TerminalBellRang(A, paneWatched: true));
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));

        Assert.True(sut.TerminalBellRang(A, paneWatched: false));
        Assert.False(sut.TerminalBellRang(A, paneWatched: false));
        Assert.Equal(PaneActivityKind.Bell, sut.AggregateTerminal(out _));
        Assert.Equal("🔔 呼び出し", PaneActivityPresentation.BadgeText(PaneActivityKind.Bell, true, false, false, 0, ""));

        sut.MarkSeen(PaneKind.Terminal);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void ベルは作業中のClaudeより先に出る()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(A, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalTitleChanged(A, "◐ Fix", paneWatched: false);
        sut.TerminalBellRang(B, paneWatched: false);

        Assert.Equal(PaneActivityKind.Bell, sut.AggregateTerminal(out _));
    }
}
