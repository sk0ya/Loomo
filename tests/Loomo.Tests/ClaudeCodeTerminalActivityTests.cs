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
    public void claudeが実行中なら印の無いタイトルでは状態を捨てず終了で捨てる()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(Tab, commandExecuted: true, commandDone: false, null, paneWatched: false);
        sut.TerminalTitleChanged(Tab, "◐ Fix tests", false);
        sut.TerminalTitleChanged(Tab, "vim", false); // claude の中で動いた別のプログラムがタイトルを書いた
        Assert.Equal(PaneActivityKind.AgentWorking, sut.AggregateTerminal(out _));

        sut.TerminalCommandChanged(Tab, commandExecuted: false, commandDone: true, 0, paneWatched: true);
        Assert.Equal(PaneActivityKind.None, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void 前面でコマンドかClaudeが動いている間は打ち込み不可()
    {
        var shell = Guid.NewGuid();
        var sut = new PaneActivityTracker();
        Assert.False(sut.IsTerminalBusy(shell)); // 一度も動いていないタブは空いている
        sut.TerminalCommandChanged(shell, commandExecuted: true, commandDone: false, null, paneWatched: true);
        Assert.True(sut.IsTerminalBusy(shell));
        sut.TerminalCommandChanged(shell, commandExecuted: false, commandDone: true, 0, paneWatched: true);
        Assert.False(sut.IsTerminalBusy(shell));

        // シェル統合の無いところで起動した claude（C を見ていない）でも、印があれば埋まっている。
        sut.TerminalTitleChanged(Tab, "✳ Claude Code", paneWatched: true);
        Assert.True(sut.IsTerminalBusy(Tab));
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

/// <summary>エディタの場所を Claude Code へ送る参照（§24.3.1）。</summary>
public sealed class ClaudeCodeReferenceTests
{
    [Fact]
    public void 作業場所の下は相対パスで区切りはスラッシュ()
        => Assert.Equal("@src/App/Foo.cs#L10-24 ",
            ClaudeCodeReference.Format(@"C:\repo\src\App\Foo.cs", @"C:\repo", 9, 23));

    [Fact]
    public void 一行だけなら範囲にしない_選択なしはファイルだけ()
    {
        Assert.Equal("@Foo.cs#L3 ", ClaudeCodeReference.Format(@"C:\repo\Foo.cs", @"C:\repo", 2, 2));
        Assert.Equal("@Foo.cs ", ClaudeCodeReference.Format(@"C:\repo\Foo.cs", @"C:\repo", null, null));
    }

    [Fact]
    public void 作業場所の外や別ドライブは絶対パス()
    {
        Assert.Equal(@"@C:\other\Foo.cs ", ClaudeCodeReference.Format(@"C:\other\Foo.cs", @"C:\repo", null, null));
        Assert.Equal(@"@D:\x\Foo.cs ", ClaudeCodeReference.Format(@"D:\x\Foo.cs", @"C:\repo", null, null));
        Assert.Equal(@"@C:\repo2\Foo.cs ", ClaudeCodeReference.Format(@"C:\repo2\Foo.cs", @"C:\repo", null, null));
    }

    [Fact]
    public void 空白を含むパスは引用符で囲む()
        => Assert.Equal("@\"my docs/a b.md\"#L1 ",
            ClaudeCodeReference.Format(@"C:\repo\my docs\a b.md", @"C:\repo", 0, 0));

    [Fact]
    public void 次の行の頭で終わる選択はその行を含めない()
    {
        Assert.Equal((4, 6), ClaudeCodeReference.LinesOfSelection(4, 7, 0));
        Assert.Equal((4, 7), ClaudeCodeReference.LinesOfSelection(4, 7, 3));
        Assert.Equal((4, 4), ClaudeCodeReference.LinesOfSelection(4, 4, 0));
    }

    [Fact]
    public void 見出しは短く1始まり()
    {
        Assert.Equal("Foo.cs:10-24", ClaudeCodeReference.Label(@"C:\repo\Foo.cs", 9, 23));
        Assert.Equal("Foo.cs", ClaudeCodeReference.Label(@"C:\repo\Foo.cs", null, null));
    }

    [Fact]
    public void 宛先は前に出ているClaudeタブ_なければ最後に動いたClaudeタブ()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var plain = Guid.NewGuid();
        var sut = new PaneActivityTracker();
        Assert.Null(sut.AgentTabFor(plain));
        sut.TerminalTitleChanged(a, "✳ Claude Code", false);
        sut.TerminalTitleChanged(b, "◐ Fix", false);
        Assert.Equal(b, sut.AgentTabFor(plain));
        Assert.Equal(a, sut.AgentTabFor(a));
        sut.TerminalTitleChanged(a, "◐ Other", false);
        Assert.Equal(a, sut.AgentTabFor(null));
    }
}
