using System.IO;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>過去のコミットの 1 か所だけを作業ツリーで打ち消す（§24.83）。コミットの差分から選んだ行のパッチを作り、逆向きに当てる。</summary>
public sealed class GitRevertCommitLinesTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-revert-lines", Guid.NewGuid().ToString("N"));
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
        await MustRunAsync("config", "core.autocrlf", "false");
        await CommitAsync("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", "base");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 選んだ行だけが戻りコミットとステージには触らない()
    {
        var hash = await CommitAsync("1\nTWO\n3\n4\n5\n6\n7\n8\nNINE\n10\n", "edit two places");
        var patch = await _git.GetRangeFileDiffAsync(null, hash, new GitCommitFileChange('M', "a.txt", null));

        // 2 行目の書き換え（旧 2 行目の削除・新 2 行目の追加）だけを選ぶ。
        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(patch, new HashSet<int> { 2 }, new HashSet<int> { 2 });
        var result = await _git.ApplyReverseDiscardPatchAsync(reduced.Patch);

        Assert.True(result.Success, result.Message);
        Assert.Equal("1\n2\n3\n4\n5\n6\n7\n8\nNINE\n10\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal(hash, (await MustRunAsync("rev-parse", "HEAD")).Output.Trim());
        Assert.Equal(" M a.txt", (await MustRunAsync("status", "--porcelain")).Output.TrimEnd());
    }

    [Fact]
    public async Task その後に周りを書き換えていれば当たらず何も変えない()
    {
        var hash = await CommitAsync("1\nTWO\n3\n4\n5\n6\n7\n8\n9\n10\n", "edit");
        await CommitAsync("1\nTWO-again\n3\n4\n5\n6\n7\n8\n9\n10\n", "edit again");
        var patch = await _git.GetRangeFileDiffAsync(null, hash, new GitCommitFileChange('M', "a.txt", null));

        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(patch, new HashSet<int> { 2 }, new HashSet<int> { 2 });
        var result = await _git.ApplyReverseDiscardPatchAsync(reduced.Patch);

        Assert.False(result.Success);
        Assert.Equal("1\nTWO-again\n3\n4\n5\n6\n7\n8\n9\n10\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    private async Task<string> CommitAsync(string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), content);
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
