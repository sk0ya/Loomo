using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>プルの前の確認（§24.64）：上流と分かれたとき、受け取るコミットと衝突の予告を作業ツリーに触らずに引く。</summary>
public sealed class GitPullPreviewTests : IAsyncLifetime
{
    private readonly string _origin = Path.Combine(Path.GetTempPath(), "loomo-git-tests", Guid.NewGuid().ToString("N"));
    private readonly string _clone = Path.Combine(Path.GetTempPath(), "loomo-git-tests", Guid.NewGuid().ToString("N"));
    private GitService _originGit = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_origin);
        _originGit = Open(_origin);
        await MustRunAsync(_originGit, "init", "-b", "main");
        await ConfigureAsync(_originGit);
        await CommitAsync(_originGit, _origin, "shared.txt", "base\n", "base");
        await MustRunAsync(_originGit, "clone", _origin, _clone);
        _git = Open(_clone);
        await ConfigureAsync(_git);
    }

    public Task DisposeAsync()
    {
        foreach (var dir in new[] { _origin, _clone })
            try { Directory.Delete(dir, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 分かれた上流から来るコミットと衝突するファイルを作業ツリーに触らずに引く()
    {
        await CommitAsync(_originGit, _origin, "shared.txt", "theirs\n", "向こうの変更");
        await CommitAsync(_originGit, _origin, "other.txt", "x\n", "向こうの別の変更");
        await CommitAsync(_git, _clone, "shared.txt", "ours\n", "こちらの変更");
        await MustRunAsync(_git, "fetch");
        var headBefore = (await MustRunAsync(_git, "rev-parse", "HEAD")).Output;

        var commits = await _git.GetIncomingCommitsAsync("origin/main");
        var forecast = await _git.ForecastUpstreamConflictsAsync("origin/main");

        Assert.Equal(["向こうの別の変更", "向こうの変更"], commits.Select(c => c.Subject).ToArray());
        Assert.Equal(["shared.txt"], forecast.Files);
        Assert.Equal(headBefore, (await MustRunAsync(_git, "rev-parse", "HEAD")).Output);
        Assert.Equal("ours\n", (await File.ReadAllTextAsync(Path.Combine(_clone, "shared.txt"))).Replace("\r", ""));
    }

    [Fact]
    public void 確認の文言は件数と衝突を言い切る()
    {
        var commits = new[] { new GitIncomingCommit("abc1234", "向こうの変更", "誰か") };
        var conflicting = new GitPullPreview("origin/main", 3, 1, commits, ["shared.txt"]);
        Assert.Equal("origin/main と分かれています — 受け取る 3 件・こちらだけ 1 件", conflicting.Summary);
        Assert.Equal("ほか 2 件", conflicting.MoreCommits);
        Assert.Equal("⚠ 取り込むと衝突 1 ファイル", conflicting.ConflictLabel);
        Assert.Equal(["abc1234  向こうの変更 — 誰か"], conflicting.CommitLines);

        var clean = new GitPullPreview("origin/main", 1, 1, commits, []);
        Assert.Null(clean.MoreCommits);
        Assert.False(clean.HasConflicts);
        Assert.StartsWith("衝突しない見込み", clean.ConflictLabel);
    }

    private static GitService Open(string root)
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        return new GitService(workspace);
    }

    private static async Task ConfigureAsync(GitService git)
    {
        await MustRunAsync(git, "config", "user.name", "Loomo Test");
        await MustRunAsync(git, "config", "user.email", "loomo@example.invalid");
    }

    private static async Task CommitAsync(GitService git, string root, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(root, path), content);
        await MustRunAsync(git, "add", path);
        await MustRunAsync(git, "commit", "-m", message);
    }

    private static async Task<GitCommandResult> MustRunAsync(GitService git, params string[] args)
    {
        var result = await git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
