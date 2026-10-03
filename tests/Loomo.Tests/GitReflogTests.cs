using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>操作ログ（reflog）の読み分け・言い換え（純ロジック）。</summary>
public sealed class GitReflogParserTests
{
    [Theory]
    [InlineData("commit: 件名", GitReflogKind.Commit, "件名")]
    [InlineData("commit (initial): first", GitReflogKind.Commit, "最初のコミット: first")]
    [InlineData("commit (amend): 直した", GitReflogKind.Amend, "コミットを修正: 直した")]
    [InlineData("commit (merge): Merge branch 'x'", GitReflogKind.Merge, "マージをコミット: Merge branch 'x'")]
    [InlineData("checkout: moving from main to feature/a", GitReflogKind.Checkout, "main → feature/a に切り替え")]
    [InlineData("checkout: moving from main to 0123456789abcdef0123456789abcdef01234567", GitReflogKind.Checkout, "main → 0123456 に切り替え")]
    [InlineData("reset: moving to HEAD~2", GitReflogKind.Reset, "HEAD~2 へリセット")]
    [InlineData("rebase (start): checkout main", GitReflogKind.Rebase, "リベース開始（main の上へ）")]
    [InlineData("rebase (pick): 件名", GitReflogKind.Rebase, "リベースで再適用: 件名")]
    [InlineData("rebase (finish): returning to refs/heads/feature", GitReflogKind.Rebase, "リベース完了（feature へ戻る）")]
    [InlineData("rebase (abort): returning to refs/heads/feature", GitReflogKind.Rebase, "リベースを中止")]
    [InlineData("rebase -i (start): checkout HEAD~3", GitReflogKind.Rebase, "リベース開始（HEAD~3 の上へ）")]
    [InlineData("pull --rebase (pick): 件名", GitReflogKind.Rebase, "プル（リベース）で再適用: 件名")]
    [InlineData("merge feature: Fast-forward", GitReflogKind.Merge, "feature をマージ（早送り）")]
    [InlineData("merge origin/main: Merge made by the 'ort' strategy.", GitReflogKind.Merge, "origin/main をマージ")]
    [InlineData("pull: Fast-forward", GitReflogKind.Pull, "プル（早送り）")]
    [InlineData("cherry-pick: 拾った", GitReflogKind.CherryPick, "チェリーピック: 拾った")]
    [InlineData("branch: Created from HEAD", GitReflogKind.Branch, "HEAD からブランチを作成")]
    [InlineData("branch: Reset to main", GitReflogKind.Branch, "main へブランチを付け替え")]
    [InlineData("Branch: renamed refs/heads/a to refs/heads/b", GitReflogKind.Branch, "a → b に名前を変更")]
    [InlineData("clone: from https://example.invalid/r.git", GitReflogKind.Clone, "クローン: from https://example.invalid/r.git")]
    [InlineData("update by push", GitReflogKind.Sync, "プッシュ")]
    [InlineData("WIP on main: 1a2b3c4 件名", GitReflogKind.Stash, "main でスタッシュ: 1a2b3c4 件名")]
    public void メッセージを種類と日本語の説明へ読み分ける(string message, GitReflogKind kind, string description)
    {
        var (actualKind, actualDescription) = GitReflogParser.Classify(message, "件名");
        Assert.Equal(kind, actualKind);
        Assert.Equal(description, actualDescription);
    }

    [Fact]
    public void 読めないメッセージは言い換えずそのまま出す()
    {
        Assert.Equal((GitReflogKind.Other, "something new: x"), GitReflogParser.Classify("something new: x", "件名"));
        // メッセージが空（ワークツリー作成直後の記録など）は件名で代える。
        Assert.Equal((GitReflogKind.Other, "件名"), GitReflogParser.Classify("", "件名"));
    }

    [Fact]
    public void 一件多く読んだ分で最後の行の操作前を埋め続きの有無を知る()
    {
        var output = string.Concat(
            Record("c3", "HEAD@{2026-10-03T12:00:00+09:00}", "reset: moving to HEAD~1", "s3"),
            Record("c2", "HEAD@{2026-10-03T11:00:00+09:00}", "commit: s2", "s2"),
            Record("c1", "HEAD@{2026-10-03T10:00:00+09:00}", "commit: s1", "s1"));

        var page = GitReflogParser.Parse(output, "HEAD", skip: 5, take: 2);

        Assert.True(page.HasMore);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal("HEAD@{5}", page.Entries[0].Selector);
        Assert.Equal("c2", page.Entries[0].PreviousHash);
        Assert.Equal("c1", page.Entries[1].PreviousHash);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(9)), page.Entries[0].Time);
        Assert.Equal(GitReflogKind.Reset, page.Entries[0].Kind);
    }

    [Fact]
    public void 最古の記録には操作前が無い()
    {
        var page = GitReflogParser.Parse(Record("c1", "HEAD@{2026-10-03T10:00:00+09:00}", "commit (initial): s1", "s1"),
            "HEAD", 0, 10);

        var entry = Assert.Single(page.Entries);
        Assert.False(page.HasMore);
        Assert.Null(entry.PreviousHash);
        Assert.False(entry.MovedCommit);
    }

    private static string Record(string hash, string selector, string message, string subject) =>
        $"\x1e{hash}\x1f{hash}\x1f{selector}\x1f{message}\x1f{subject}\x1fLoomo Test\n";
}

/// <summary>操作ログの一覧表示（日付の区切り・時刻・絞り込み）。</summary>
public sealed class GitReflogRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 日付の見出しは今日昨日それ以前で言い分ける()
    {
        Assert.Equal("今日", GitReflogTimeFormat.Day(Now.AddHours(-3), Now));
        Assert.Equal("昨日", GitReflogTimeFormat.Day(Now.AddDays(-1), Now));
        Assert.Equal("9月12日（土）", GitReflogTimeFormat.Day(new DateTimeOffset(2026, 9, 12, 9, 0, 0, Now.Offset), Now));
        Assert.Equal("2025年12月31日（水）", GitReflogTimeFormat.Day(new DateTimeOffset(2025, 12, 31, 9, 0, 0, Now.Offset), Now));
    }

    [Fact]
    public void 相対時刻()
    {
        Assert.Equal("たった今", GitReflogTimeFormat.Relative(Now.AddSeconds(-10), Now));
        Assert.Equal("5分前", GitReflogTimeFormat.Relative(Now.AddMinutes(-5), Now));
        Assert.Equal("3時間前", GitReflogTimeFormat.Relative(Now.AddHours(-3), Now));
        Assert.Equal("2日前", GitReflogTimeFormat.Relative(Now.AddDays(-2), Now));
    }

    [Fact]
    public void 絞り込みは語のANDで説明件名ハッシュ作者に効き辿れないものだけにもできる()
    {
        var reset = Row(GitReflogKind.Reset, "HEAD~1 へリセット", "abc1234ffff", "件名A", lost: true);
        var commit = Row(GitReflogKind.Commit, "直した", "def5678ffff", "直した", lost: false);

        Assert.True(GitReflogViewModel.Matches(reset, "リセット", lostOnly: false));
        Assert.True(GitReflogViewModel.Matches(reset, "abc12", lostOnly: false));
        Assert.True(GitReflogViewModel.Matches(reset, "件名a リセット", lostOnly: false));
        Assert.False(GitReflogViewModel.Matches(reset, "件名a コミット", lostOnly: false));
        Assert.True(GitReflogViewModel.Matches(commit, "loomo", lostOnly: false));
        Assert.True(GitReflogViewModel.Matches(reset, "", lostOnly: true));
        Assert.False(GitReflogViewModel.Matches(commit, "", lostOnly: true));
    }

    [Fact]
    public void 操作の流用はコミット一覧の行の形にする()
    {
        var row = Row(GitReflogKind.Reset, "HEAD~1 へリセット", "abc1234ffff", "件名A", lost: false);
        var log = row.ToLogRow();
        Assert.Equal("abc1234ffff", log.Hash);
        Assert.Equal("abc1234", log.ShortHash);
        Assert.Equal("件名A", log.Subject);
    }

    private static GitReflogRow Row(GitReflogKind kind, string description, string hash, string subject, bool lost) =>
        new(new GitReflogEntry("HEAD", 0, hash, hash[..7], Now.AddHours(-1), kind,
            "raw", description, subject, "Loomo Test"), lost, Now);
}

/// <summary>実際の git に対する操作ログの読み込みと「辿れない」判定。</summary>
public sealed class GitReflogServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-reflog", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task コミットがまだ無いリポジトリは失敗ではなく空()
    {
        var page = await _git.GetReflogAsync("HEAD", 0, 10);
        Assert.Empty(page.Entries);
        Assert.Null(page.Error);
    }

    [Fact]
    public async Task リセットで外れたコミットが新しい順に並び辿れないと分かる()
    {
        var first = await CommitAsync("a.txt", "1\n", "first");
        var second = await CommitAsync("a.txt", "2\n", "second");
        await MustRunAsync("reset", "--hard", first);

        var page = await _git.GetReflogAsync("HEAD", 0, 10);

        Assert.Equal(3, page.Entries.Count);
        Assert.False(page.HasMore);
        var reset = page.Entries[0];
        Assert.Equal(GitReflogKind.Reset, reset.Kind);
        Assert.Equal(first, reset.Hash);
        Assert.Equal(second, reset.PreviousHash);
        Assert.True(reset.MovedCommit);
        Assert.NotNull(reset.Time);
        Assert.Equal("second", page.Entries[1].Description);
        Assert.Equal("最初のコミット: first", page.Entries[2].Description);

        var lost = await _git.GetUnreachableCommitsAsync(page.Entries.Select(e => e.Hash));
        Assert.Equal(new[] { second }, lost.ToArray());
    }

    [Fact]
    public async Task ページを分けても番号と操作前が繋がる()
    {
        for (var i = 0; i < 5; i++)
            await CommitAsync("a.txt", $"{i}\n", $"c{i}");

        var head = await _git.GetReflogAsync("HEAD", 0, 2);
        var next = await _git.GetReflogAsync("HEAD", 2, 2);

        Assert.True(head.HasMore);
        Assert.Equal(next.Entries[0].Hash, head.Entries[1].PreviousHash);
        Assert.Equal("HEAD@{2}", next.Entries[0].Selector);
        Assert.Equal("c2", next.Entries[0].Subject);
    }

    [Fact]
    public async Task ブランチの操作ログも読める()
    {
        await CommitAsync("a.txt", "1\n", "first");
        await MustRunAsync("branch", "feature");

        var page = await _git.GetReflogAsync("feature", 0, 10);

        var created = Assert.Single(page.Entries);
        Assert.Equal(GitReflogKind.Branch, created.Kind);
        Assert.Equal("feature@{0}", created.Selector);
    }

    private async Task<string> CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
