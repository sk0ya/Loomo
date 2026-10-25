using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>プッシュ済みのコミットを書き換える前に、そうだと言う（§24.78）。リモートは <c>refs/remotes/</c> を直接置いて真似る。</summary>
public sealed class GitPublishedHistoryTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-published", Guid.NewGuid().ToString("N"));
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
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task リモートにあるコミットだけを名指しする()
    {
        var first = await CommitAsync("a.txt", "a\n", "first");
        await MustRunAsync("update-ref", "refs/remotes/origin/main", first);
        await MustRunAsync("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        var second = await CommitAsync("a.txt", "b\n", "second");

        Assert.Equal(["origin/main"], await _git.GetRemoteBranchesContainingAsync(first));
        Assert.Empty(await _git.GetRemoteBranchesContainingAsync(second));
    }

    [Fact]
    public async Task リベースで作り直すいちばん古いコミット()
    {
        var root = await CommitAsync("a.txt", "a\n", "root");
        await MustRunAsync("branch", "base");
        var own1 = await CommitAsync("b.txt", "b\n", "own1");
        await CommitAsync("c.txt", "c\n", "own2");

        Assert.Equal(own1, await _git.GetOldestRewrittenByRebaseAsync("base"));
        Assert.Null(await _git.GetOldestRewrittenByRebaseAsync("main"));
        Assert.NotEqual(root, own1);
    }

    [Fact]
    public void 別名と切り離しの行は数えない()
    {
        var names = GitPublishedHistory.ParseRemoteBranches(
            "origin/HEAD\norigin/main\n(HEAD detached at 1234567)\nupstream/feature\norigin/main\n");

        Assert.Equal(["origin/main", "upstream/feature"], names);
    }

    [Fact]
    public void プッシュされていなければ何も言わず_多ければ件数で畳む()
    {
        Assert.Null(GitPublishedHistory.Describe([]));
        Assert.Equal("確認", GitPublishedHistory.Prefix(null, "確認"));

        var many = GitPublishedHistory.Describe(["a/x", "a/y", "a/z", "b/w", "b/v"])!;
        Assert.Contains("a/x、a/y、a/z ほか 2 件", many);
        Assert.StartsWith("⚠", GitPublishedHistory.Prefix(many, "確認"));
        Assert.EndsWith("\n\n確認", GitPublishedHistory.Prefix(many, "確認"));
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
