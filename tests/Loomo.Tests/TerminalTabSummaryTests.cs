using System;
using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>TABS のターミナル行の 2 行目（§24.53）——場所・ブランチ・前面のコマンド・最後のコマンドの畳み方と、
/// <c>.git/HEAD</c> の読み取り。</summary>
public class TerminalTabSummaryTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "loomo-ws");

    [Fact]
    public void Line_shows_place_branch_last_command_badge_and_age()
    {
        var status = new TerminalTabStatus(
            WorkingDirectory: Path.Combine(Root, "src", "app"),
            Head: new GitHeadInfo(Root, "main", false),
            LastRun: new RecentTerminalCommandRun("dotnet test", 1, Now.AddMinutes(-3)));

        Assert.Equal("src/app (main) · dotnet test ✗ 1 · 3分前", TerminalTabSummary.Line(status, [Root], Now));
    }

    [Fact]
    public void Line_while_running_shows_command_and_elapsed()
    {
        var status = new TerminalTabStatus(
            WorkingDirectory: Root,
            RunningCommand: "npm run dev",
            RunningSinceUtc: Now.AddSeconds(-83));

        // 根は相対パスが空になるのでフォルダー名で出す。
        Assert.Equal("loomo-ws · ▶ npm run dev · 1分23秒", TerminalTabSummary.Line(status, [Root], Now));
    }

    [Fact]
    public void Line_outside_workspace_uses_folder_name_and_detached_head()
    {
        var status = new TerminalTabStatus(
            WorkingDirectory: @"D:\other\tools",
            Head: new GitHeadInfo(@"D:\other", "abc1234", true));

        Assert.Equal("tools (@abc1234)", TerminalTabSummary.Line(status, [Root], Now));
    }

    [Fact]
    public void Line_is_empty_when_nothing_is_known()
        => Assert.Equal("", TerminalTabSummary.Line(TerminalTabStatus.Empty, [Root], Now));

    [Fact]
    public void Shorten_folds_multiline_and_truncates()
    {
        Assert.Equal("a ⏎ b", TerminalTabSummary.Shorten("a\r\nb"));
        var shortened = TerminalTabSummary.Shorten(new string('x', 100));
        Assert.Equal(TerminalTabSummary.MaxCommandChars, shortened.Length);
        Assert.EndsWith("…", shortened);
    }

    [Theory]
    [InlineData(5, "5秒")]
    [InlineData(125, "2分05秒")]
    [InlineData(3 * 3600 + 7 * 60, "3時間07分")]
    public void Elapsed_formats(int seconds, string expected)
        => Assert.Equal(expected, TerminalTabSummary.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Detail_lists_full_values()
    {
        var status = new TerminalTabStatus(
            WorkingDirectory: Root,
            Head: new GitHeadInfo(Root, "feature/x", false),
            LastRun: new RecentTerminalCommandRun("git status", 0, Now.AddMinutes(-1)));

        var detail = TerminalTabSummary.Detail(status, Now);
        Assert.Contains($"場所: {Root}", detail);
        Assert.Contains("ブランチ: feature/x", detail);
        Assert.Contains("最後のコマンド: git status", detail);
        Assert.Contains("終了コード: 0（成功）", detail);
    }

    [Fact]
    public void Tabs_view_model_sets_summary_flags()
    {
        var tabs = new TabsViewModel();
        var id = Guid.NewGuid();
        tabs.AddTerminalTab(id, "pwsh", true);
        var row = tabs.TerminalTabs[0];
        Assert.False(row.HasTerminalSummary);

        tabs.SetTerminalStatus(id, new TerminalTabStatus(
            LastRun: new RecentTerminalCommandRun("make", 2, Now)), [Root], Now);
        Assert.True(row.HasTerminalSummary);
        Assert.True(row.IsTerminalLastFailed);
        Assert.False(row.IsTerminalRunning);

        tabs.SetTerminalStatus(id, new TerminalTabStatus(RunningCommand: "make", RunningSinceUtc: Now), [Root], Now);
        Assert.True(row.IsTerminalRunning);
        Assert.False(row.IsTerminalLastFailed);
    }

    // ===== .git/HEAD =====

    [Theory]
    [InlineData("ref: refs/heads/main\n", "main", false)]
    [InlineData("ref: refs/heads/feature/a-b", "feature/a-b", false)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456", true)]
    public void Parse_reads_branch_or_detached(string text, string branch, bool detached)
    {
        var head = GitHeadReader.Parse(text, Root);
        Assert.NotNull(head);
        Assert.Equal(branch, head!.Branch);
        Assert.Equal(detached, head.IsDetached);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("ref: ")]
    public void Parse_rejects_unknown(string text) => Assert.Null(GitHeadReader.Parse(text, Root));

    [Fact]
    public void Read_walks_up_and_follows_worktree_gitdir()
    {
        var temp = Directory.CreateTempSubdirectory("loomo-head-");
        try
        {
            var repo = Path.Combine(temp.FullName, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git", "worktrees", "wt"));
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "wt", "HEAD"), "ref: refs/heads/topic\n");
            var nested = Path.Combine(repo, "src", "deep");
            Directory.CreateDirectory(nested);

            var main = GitHeadReader.Read(nested);
            Assert.Equal("main", main?.Branch);
            Assert.Equal(repo, main?.WorkTree);

            var wt = Path.Combine(temp.FullName, "wt");
            Directory.CreateDirectory(wt);
            File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(repo, ".git", "worktrees", "wt")}\n");
            Assert.Equal("topic", GitHeadReader.Read(wt)?.Branch);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
