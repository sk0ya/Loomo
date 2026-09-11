using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミット一覧の右クリックから増えた操作——コミットを作らない取り込み／打ち消しを、
/// 実際の git に対して確かめる。
/// </summary>
[Collection(GitProcessTests.Name)]
public sealed class GitCommitMenuActionsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "loomo-git-commit-menu-tests", Guid.NewGuid().ToString("N"));
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
        try { Directory.Delete(_root, recursive: true); }
        catch { /* git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    // ===== コミットを作らない取り込み・打ち消し =====

    [Fact]
    public async Task チェリーピックはコミットせずに変更だけ取り込める()
    {
        await CommitAsync("a.txt", "a", "first");
        await MustRunAsync("switch", "-c", "feature");
        var pick = await CommitAsync("b.txt", "b", "feature の変更");
        await MustRunAsync("switch", "main");
        var headBefore = await HeadAsync();

        var result = await _git.CherryPickNoCommitAsync(pick);

        Assert.True(result.Success, result.Message);
        Assert.Equal(headBefore, await HeadAsync());                        // コミットは増えていない
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(_root, "b.txt")));
        var status = await _git.GetStatusAsync();
        Assert.Contains(status.Staged, entry => entry.Path == "b.txt");     // ステージ済みで残る
    }

    [Fact]
    public async Task リバートはコミットせずに打ち消しだけ作業ツリーへ入れられる()
    {
        await CommitAsync("a.txt", "a", "first");
        var second = await CommitAsync("a.txt", "a2", "second");
        var headBefore = await HeadAsync();

        var result = await _git.RevertNoCommitAsync(second);

        Assert.True(result.Success, result.Message);
        Assert.Equal(headBefore, await HeadAsync());                        // 打ち消しコミットは作られない
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        var status = await _git.GetStatusAsync();
        Assert.Contains(status.Staged, entry => entry.Path == "a.txt");
    }

    private async Task<string> HeadAsync() =>
        (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();

    private async Task<string> CommitAsync(string name, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, name), content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
        return await HeadAsync();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
