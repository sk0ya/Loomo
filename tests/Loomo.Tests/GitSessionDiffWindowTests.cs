using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペインのコミット詳細で変更ファイルをダブルクリックしたときの要求
/// （<see cref="GitSessionViewModel.RequestChangedFileDiffWindow"/>）。ウィンドウを開くのは
/// ShellWindow の仕事なので、ここで確かめるのは「何を渡すか」と「渡さない条件」。
/// </summary>
public sealed class GitSessionDiffWindowTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-diff-window", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 選択中のコミットと絶対パスを載せて要求する()
    {
        await CommitAsync("src/app/main.cs", "a\n", "first");
        var vm = CreateSession();
        var commit = await HeadRowAsync();
        vm.History.SelectedLogRow = commit;

        CommitFileDiffRequest? received = null;
        vm.DiffWindowRequested += (_, r) => received = r;

        vm.RequestChangedFileDiffWindow("src/app/main.cs");

        var request = Assert.NotNull(received);
        Assert.Equal(commit.Hash, request.Hash);
        Assert.Contains(commit.ShortHash!, request.Label);
        // 削除済みファイルでも差分は見られるので、実体の有無ではなくルート基準で素直に解決する。
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "src/app/main.cs")), request.FullPath);
    }

    [Fact]
    public async Task 削除されたファイルでも要求する()
    {
        await CommitAsync("gone.txt", "x\n", "add");
        File.Delete(Path.Combine(_root, "gone.txt"));
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "remove");

        var vm = CreateSession();
        vm.History.SelectedLogRow = await HeadRowAsync();

        CommitFileDiffRequest? received = null;
        vm.DiffWindowRequested += (_, r) => received = r;

        vm.RequestChangedFileDiffWindow("gone.txt");

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "gone.txt")), Assert.NotNull(received).FullPath);
    }

    [Fact]
    public void コミットを選んでいなければ何も要求しない()
    {
        var vm = CreateSession();
        var raised = false;
        vm.DiffWindowRequested += (_, _) => raised = true;

        vm.RequestChangedFileDiffWindow("any.txt");

        Assert.False(raised);
    }

    [Fact]
    public async Task コミットの差分は表示要求として渡しDiffペインの状態には触らない()
    {
        // Diff ペインへ出すか（隠れていれば）別ウィンドウで開くかは ShellWindow が決めるので、
        // Git 側は「何を見せるか」だけを渡す——ここで VM を直に書き換えていた頃は、ペインが
        // 隠れているときの逃げ道が選べなかった。
        await CommitAsync("a.txt", "a\n", "first");
        var vm = CreateSession();
        var commit = await HeadRowAsync();

        DiffOpenTarget? received = null;
        vm.DiffOpenRequested += (_, t) => received = t;

        vm.OpenDiffForCommits(new[] { commit });

        var target = Assert.IsType<DiffOpenTarget.CommitRange>(received);
        Assert.Null(target.FromHash);                 // 1件なら「そのコミットの変更」
        Assert.Equal(commit.Hash, target.ToHash);
        Assert.Contains(commit.ShortHash!, target.Label);
    }

    [Fact]
    public async Task 複数選択中は最古の親から最新までの範囲で要求する()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await CommitAsync("b.txt", "b\n", "second");
        await CommitAsync("c.txt", "c\n", "third");
        var rows = (await _git.GetLogAsync(limit: 3)).Where(r => r.IsCommit).ToList();   // 新しい順
        var vm = CreateSession();
        vm.History.SelectedLogRow = rows[0];
        vm.History.SetSelectedCommits(new[] { rows[0], rows[1] });

        CommitFileDiffRequest? received = null;
        vm.DiffWindowRequested += (_, r) => received = r;
        vm.RequestChangedFileDiffWindow("b.txt");

        var request = Assert.NotNull(received);
        Assert.Equal(rows[0].Hash, request.Hash);
        // 起点は最古（second）そのものではなくその親＝ second 自身の変更も範囲に入る。
        Assert.Equal(rows[2].Hash, request.FromHash);
    }

    [Fact]
    public async Task 複数選択の詳細は選んだコミットぶんの変更ファイルを出す()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await CommitAsync("b.txt", "b\n", "second");
        await CommitAsync("c.txt", "c\n", "third");
        var rows = (await _git.GetLogAsync(limit: 3)).Where(r => r.IsCommit).ToList();
        var history = CreateSession().History;
        history.SelectedLogRow = rows[0];
        history.SetSelectedCommits(new[] { rows[0], rows[1] });

        await WaitUntilAsync(() => history.CommitFiles.Count == 2);
        var names = history.CommitFiles.Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "b.txt", "c.txt" }, names);
        Assert.Contains("2 コミットを選択", history.CommitMessage);
        Assert.Contains(rows[1].Subject!, history.CommitMessage);
        Assert.DoesNotContain("未選択", history.CommitMessage);

        // 1件へ戻せば、そのコミットだけの詳細に戻る。
        history.SetSelectedCommits(new[] { rows[0] });
        await WaitUntilAsync(() => history.CommitFiles.Count == 1);
        Assert.Null(history.SelectedRange);
    }

    [Fact]
    public async Task 飛び飛びに選ぶと範囲に未選択コミットが含まれることを書く()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await CommitAsync("b.txt", "b\n", "second");
        await CommitAsync("c.txt", "c\n", "third");
        var rows = (await _git.GetLogAsync(limit: 3)).Where(r => r.IsCommit).ToList();
        var history = CreateSession().History;
        history.SelectedLogRow = rows[0];
        history.SetSelectedCommits(new[] { rows[0], rows[2] });   // ルートコミットも含む

        await WaitUntilAsync(() => history.CommitFiles.Count == 3);
        Assert.Contains("未選択の 1 コミット", history.CommitMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    // ===== ヘルパー =====

    private GitSessionViewModel CreateSession()
    {
        var editor = new FakeEditorService();
        var query = new GitSessionQuery(_git);
        return new GitSessionViewModel(_git, editor, query, new GitSessionCommandHandler(_git),
            new GitHistoryViewModel(query), new GitRootSwitchViewModel(_git, _workspace), null, null);
    }

    /// <summary>HEAD のコミット行。<see cref="GitHistoryViewModel.ReloadAsync"/> は経由しない——
    /// 一覧は WPF の CollectionView を持っていて、Dispatcher の無いテストスレッドからは変更できない。
    /// ここで要るのは「選択中のコミット」1件だけなので、git から直接取る。</summary>
    private async Task<GitLogRow> HeadRowAsync()
    {
        var rows = await _git.GetLogAsync(limit: 1);
        return rows.First(r => r.IsCommit);
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
