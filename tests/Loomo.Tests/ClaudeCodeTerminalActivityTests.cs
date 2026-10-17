using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ターミナルで動く Claude Code の作業中／入力待ちを、タイトルの印から袖のバッジへ出す（§24.1.1）。
/// タイトルの列は Claude Code 2.1.302 を ConPTY 越しに採取した実物。</summary>
public sealed class ClaudeCodeTerminalActivityTests
{
    private static readonly Guid Tab = Guid.NewGuid();

    [Theory]
    [InlineData("✳ Claude Code", false)]
    [InlineData("◐ Claude Code", true)]
    [InlineData("◑ Loomo-probe.tmp file creation", true)]
    [InlineData("✳ Loomo-probe.tmp file creation", false)]
    public void タイトルの印から状態を読む(string title, bool working)
        => Assert.Equal(working ? ClaudeCodeStatus.Working : ClaudeCodeStatus.Idle, ClaudeCodeTitle.Parse(title));

    [Theory]
    [InlineData("")]
    [InlineData("claude")]
    [InlineData("pwsh")]
    [InlineData("✳")]
    [InlineData("✳Claude")]
    [InlineData(null)]
    public void Claude_Codeのタイトルでなければ読まない(string? title)
        => Assert.Null(ClaudeCodeTitle.Parse(title));

    [Fact]
    public void 見ていない間にターンが終わると入力待ちが残る()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(Tab, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalTitleChanged(Tab, "✳ Claude Code", paneWatched: false);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _)); // 開いているだけなら「実行中」を出さない

        sut.TerminalTitleChanged(Tab, "◐ Fix tests", paneWatched: false);
        Assert.Equal(PaneActivityKind.AgentWorking, sut.AggregateTerminal(out _));

        var ended = sut.TerminalTitleChanged(Tab, "✳ Fix tests", paneWatched: false);
        Assert.True(ended.TurnEnded);
        Assert.Equal(PaneActivityKind.AgentWaiting, sut.AggregateTerminal(out _));

        sut.MarkSeen(PaneKind.Terminal);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void 見ている間に終わったターンは残さない()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalTitleChanged(Tab, "◐ Fix tests", paneWatched: true);
        var ended = sut.TerminalTitleChanged(Tab, "✳ Fix tests", paneWatched: true);
        Assert.True(ended.TurnEnded); // 呼び戻し（タスクバー）の判断はウィンドウ側に任せる
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void 印のコマ送りでは再描画しない()
    {
        var sut = new PaneActivityTracker();
        Assert.True(sut.TerminalTitleChanged(Tab, "◐ Fix tests", false).ShouldRefresh);
        Assert.False(sut.TerminalTitleChanged(Tab, "◑ Fix tests", false).ShouldRefresh);
        Assert.False(sut.TerminalTitleChanged(Tab, "◐ Fix tests", false).ShouldRefresh);
    }

    [Fact]
    public void 待機から始まった最初の印はターンの終わりとみなさない()
    {
        var sut = new PaneActivityTracker();
        Assert.False(sut.TerminalTitleChanged(Tab, "✳ Claude Code", false).TurnEnded);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void Claudeを抜けたら状態を捨てる()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalTitleChanged(Tab, "◐ Fix tests", false);
        sut.TerminalTitleChanged(Tab, "✳ Fix tests", false);
        Assert.True(sut.TerminalTitleChanged(Tab, "pwsh", false).ShouldRefresh);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void claudeが実行中なら読めない見出しでは状態を捨てず終了で捨てる()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(Tab, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalTitleChanged(Tab, "◐ Fix tests", false);
        sut.TerminalTitleChanged(Tab, "pwsh", false); // 「◐ Fix src/foo.cs」はパス扱いでプロファイル名に化ける
        Assert.Equal(PaneActivityKind.AgentWorking, sut.AggregateTerminal(out _));

        sut.TerminalCommandChanged(Tab, commandExecuted: false, commandDone: true, 0, paneWatched: true);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void 入力待ちは他タブの実行中や失敗より先に出る()
    {
        var other = Guid.NewGuid();
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(other, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalTitleChanged(Tab, "◐ Fix tests", false);
        sut.TerminalTitleChanged(Tab, "✳ Fix tests", false);
        Assert.Equal(PaneActivityKind.AgentWaiting, sut.AggregateTerminal(out _));
        Assert.Equal("✳ Claude 入力待ち", PaneActivityPresentation.BadgeText(
            PaneActivityKind.AgentWaiting, isTerminal: true, isIde: false, ideTaskRunning: false, 0, ""));
    }
}
