using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class GitInteractiveRebaseTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-rebase-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 順序を入れ替えられる()
    {
        var a = await CommitAsync("a.txt", "a", "commit-a");
        await CommitAsync("b.txt", "b", "commit-b");
        await CommitAsync("c.txt", "c", "commit-c");

        var (entries, error) = await _git.GetRebaseCandidatesAsync(a);
        Assert.Null(error);
        Assert.Equal(new[] { "commit-a", "commit-b", "commit-c" }, entries.Select(e => e.Subject));

        // a, b, c → b, a, c に並び替え
        var reordered = new[] { entries[1], entries[0], entries[2] };
        var result = await _git.InteractiveRebaseAsync(a, reordered, new Dictionary<string, string>());

        Assert.True(result.Success, result.Error);
        var subjects = await GetSubjectsAsync();
        Assert.Equal(new[] { "commit-a", "commit-b", "commit-c" }, subjects.OrderBy(s => s)); // 内容は変わらない
        Assert.Equal(new[] { "commit-b", "commit-a", "commit-c" }, subjects);
    }

    [Fact]
    public async Task dropしたコミットが履歴から消える()
    {
        var a = await CommitAsync("a.txt", "a", "commit-a");
        await CommitAsync("b.txt", "b", "commit-b");
        await CommitAsync("c.txt", "c", "commit-c");

        var (entries, error) = await _git.GetRebaseCandidatesAsync(a);
        Assert.Null(error);
        var plan = new[] { entries[0], entries[1] with { Action = RebaseAction.Drop }, entries[2] };

        var result = await _git.InteractiveRebaseAsync(a, plan, new Dictionary<string, string>());

        Assert.True(result.Success, result.Error);
        var subjects = await GetSubjectsAsync();
        Assert.Equal(new[] { "commit-a", "commit-c" }, subjects);
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task 複数のrewordエントリがそれぞれ別のメッセージになる()
    {
        var a = await CommitAsync("a.txt", "a", "commit-a");
        await CommitAsync("b.txt", "b", "commit-b");
        await CommitAsync("c.txt", "c", "commit-c");

        var (entries, error) = await _git.GetRebaseCandidatesAsync(a);
        Assert.Null(error);
        var plan = new[]
        {
            entries[0] with { Action = RebaseAction.Reword },
            entries[1],
            entries[2] with { Action = RebaseAction.Reword },
        };
        var messages = new Dictionary<string, string>
        {
            [entries[0].Hash] = "REWORDED-A",
            [entries[2].Hash] = "REWORDED-C",
        };

        var result = await _git.InteractiveRebaseAsync(a, plan, messages);

        Assert.True(result.Success, result.Error);
        var subjects = await GetSubjectsAsync();
        Assert.Equal(new[] { "REWORDED-A", "commit-b", "REWORDED-C" }, subjects);
    }

    [Fact]
    public async Task マージコミットを含む範囲は拒否されリポジトリが変化しない()
    {
        var a = await CommitAsync("a.txt", "a", "commit-a");
        await MustRunAsync("switch", "-c", "feature");
        await CommitAsync("feature.txt", "f", "commit-feature");
        await MustRunAsync("switch", "-");
        await CommitAsync("c.txt", "c", "commit-c");
        await MustRunAsync("merge", "--no-edit", "feature");
        var headBefore = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();

        var (entries, error) = await _git.GetRebaseCandidatesAsync(a);

        Assert.Empty(entries);
        Assert.NotNull(error);
        var headAfter = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        Assert.Equal(headBefore, headAfter);
    }

    [Fact]
    public async Task コンフリクトで一時停止し解決後continueで完了し後始末される()
    {
        // commit1(root): line2 を後続コミットが書き換える → commit2 を drop すると
        // commit3 の diff（line2-mod → line2-mod-again）が文脈不一致でコンフリクトになる。
        var first = await CommitAsync("file.txt", "line1\nline2\nline3\n", "first");
        await CommitAsync("file.txt", "line1\nline2-mod\nline3\n", "second");
        await CommitAsync("file.txt", "line1\nline2-mod-again\nline3\n", "third");

        var (entries, error) = await _git.GetRebaseCandidatesAsync(first);
        Assert.Null(error);
        var plan = new[]
        {
            entries[0] with { Action = RebaseAction.Reword },
            entries[1] with { Action = RebaseAction.Drop },
            entries[2],
        };
        var messages = new Dictionary<string, string> { [entries[0].Hash] = "REWORDED-first" };

        var result = await _git.InteractiveRebaseAsync(first, plan, messages);
        Assert.False(result.Success);

        var status = await _git.GetStatusAsync();
        Assert.True(status.RebaseInProgress);

        var gitDir = (await MustRunAsync("rev-parse", "--git-dir")).Output.Trim();
        var msgFile = Path.Combine(_root, gitDir, $"loomo-rebase-msg-{entries[0].Hash}.txt");
        Assert.True(File.Exists(msgFile), "コンフリクト中は reword 用ファイルが残っているべき");

        // コンフリクトを解決して続行
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "line1\nline2-mod-again\nline3\n");
        await MustRunAsync("add", "file.txt");
        var continueResult = await _git.RebaseContinueAsync();

        Assert.True(continueResult.Success, continueResult.Error);
        var subjects = await GetSubjectsAsync();
        Assert.Equal(new[] { "REWORDED-first", "third" }, subjects);
        Assert.False(File.Exists(msgFile), "続行後は一時ファイルが片付くべき");
        Assert.False((await _git.GetStatusAsync()).RebaseInProgress);
    }

    private async Task<string[]> GetSubjectsAsync() =>
        (await MustRunAsync("log", "--reverse", "--format=%s")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).ToArray();

    private async Task<string> CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
