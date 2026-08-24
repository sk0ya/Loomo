using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>上流（追跡先）の解決。<c>upstream:short</c> の文字列を割るのではなく
/// <c>branch.&lt;name&gt;.remote</c>／<c>.merge</c> を正本にする、という約束を守らせる。</summary>
public sealed class GitUpstreamResolutionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "loomo-git-upstream-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init", "-b", "main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ローカル上流の名前がリモート名と紛らわしくても取り違えない()
    {
        // 「topic/base」を上流に持つ feature。リモート名 topic も居るので、名前を割る方式だと
        // remote=topic / branch=base と読み違えて、ローカルではなくリモートを触ってしまう。
        await CommitAsync("one.txt", "one", "first");
        await MustRunAsync("branch", "topic/base");
        await MustRunAsync("branch", "feature");
        await MustRunAsync("checkout", "topic/base");
        var head = await CommitAsync("two.txt", "two", "second");
        await MustRunAsync("checkout", "main");
        await MustRunAsync("branch", "--set-upstream-to=topic/base", "feature");
        await MustRunAsync("remote", "add", "topic", Path.Combine(_root, "存在しないリモート"));

        var feature = Single(await _git.GetBranchesAsync(), "feature");
        Assert.Equal("topic/base", feature.Upstream);

        var result = await _git.PullBranchAsync(feature);

        Assert.True(result.Success, result.Error);
        var updated = (await MustRunAsync("rev-parse", "refs/heads/feature")).Output.Trim();
        Assert.Equal(head, updated);
    }

    [Fact]
    public async Task 上流が解決できないときはローカルブランチを作らずに失敗する()
    {
        // git remote の照会が失敗した（＝一覧が空に見える）状況の再現。上流名だけを頼りに
        // ローカル扱いすると、push が「origin/main」という名前のローカルブランチを作ってしまう。
        await CommitAsync("one.txt", "one", "first");
        await MustRunAsync("branch", "solo");
        var solo = new GitBranchInfo("solo", IsCurrent: false, IsRemote: false, Upstream: "origin/main");

        var result = await _git.PushBranchAsync(solo, defaultRemote: null);

        Assert.False(result.Success);
        Assert.Contains("プッシュ先リモートがありません", result.Message);
        var refs = await MustRunAsync("for-each-ref", "--format=%(refname)", "refs/heads");
        Assert.DoesNotContain("refs/heads/origin/main", refs.Output);
    }

    private static GitBranchInfo Single(IReadOnlyList<GitBranchInfo> branches, string name)
        => branches.Single(b => !b.IsRemote && b.Name == name);

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
