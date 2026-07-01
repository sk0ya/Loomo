using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="GitService.MergeAsync(string, GitMergeStrategy)"/> の戦略切り替えを検証する。
/// 実際の git プロセスを起動する既存テスト（<see cref="GitConflictTests"/> 等）と同じ方式。
/// </summary>
public sealed class GitMergeStrategyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-merge-strategy-tests", Guid.NewGuid().ToString("N"));
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

        await File.WriteAllTextAsync(Path.Combine(_root, "base.txt"), "base\n");
        await MustRunAsync("add", "base.txt");
        await MustRunAsync("commit", "-m", "base");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 既定はfast_forward可能ならそのまま進める()
    {
        await MustRunAsync("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "feature.txt"), "feature\n");
        await MustRunAsync("add", "feature.txt");
        await MustRunAsync("commit", "-m", "feature");
        await MustRunAsync("switch", "-");

        var result = await _git.MergeAsync("feature");
        Assert.True(result.Success, result.Error);

        var parents = await MustRunAsync("rev-list", "--parents", "-n", "1", "HEAD");
        // fast-forward はマージコミットを作らない＝出力は「自分自身 + 親1つ」の2トークン。
        Assert.Equal(2, parents.Output.Trim().Split(' ').Length);
    }

    [Fact]
    public async Task FastForwardOnlyは分岐しているとマージコミットを作らず失敗する()
    {
        await MustRunAsync("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "feature.txt"), "feature\n");
        await MustRunAsync("add", "feature.txt");
        await MustRunAsync("commit", "-m", "feature");
        await MustRunAsync("switch", "-");

        // 現在のブランチも進める → fast-forward 不可能な状態にする。
        await File.WriteAllTextAsync(Path.Combine(_root, "main-only.txt"), "main\n");
        await MustRunAsync("add", "main-only.txt");
        await MustRunAsync("commit", "-m", "main-only");

        var beforeHead = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        var result = await _git.MergeAsync("feature", GitMergeStrategy.FastForwardOnly);
        Assert.False(result.Success);

        var afterHead = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        Assert.Equal(beforeHead, afterHead);
    }

    [Fact]
    public async Task NoFastForwardはfast_forward可能でもマージコミットを作る()
    {
        await MustRunAsync("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "feature.txt"), "feature\n");
        await MustRunAsync("add", "feature.txt");
        await MustRunAsync("commit", "-m", "feature");
        await MustRunAsync("switch", "-");

        var result = await _git.MergeAsync("feature", GitMergeStrategy.NoFastForward);
        Assert.True(result.Success, result.Error);

        var parents = await MustRunAsync("rev-list", "--parents", "-n", "1", "HEAD");
        // マージコミットは親を2つ持つ（コミットハッシュ自身 + 親2つ = 3トークン）。
        Assert.Equal(3, parents.Output.Trim().Split(' ').Length);
    }

    [Fact]
    public async Task Squashはコミットせずステージだけする()
    {
        await MustRunAsync("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "feature.txt"), "feature\n");
        await MustRunAsync("add", "feature.txt");
        await MustRunAsync("commit", "-m", "feature");
        await MustRunAsync("switch", "-");

        var beforeHead = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        var result = await _git.MergeAsync("feature", GitMergeStrategy.Squash);
        Assert.True(result.Success, result.Error);

        var afterHead = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        Assert.Equal(beforeHead, afterHead); // コミットは作られない

        var status = await _git.GetStatusAsync();
        Assert.Contains(status.Staged, e => e.Path == "feature.txt"); // ステージだけされている
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
