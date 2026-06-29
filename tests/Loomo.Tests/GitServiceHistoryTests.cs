using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class GitServiceHistoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-tests", Guid.NewGuid().ToString("N"));
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
    public async Task コミットメッセージ修正は後続コミットを保って全文を置き換える()
    {
        var first = await CommitAsync("one.txt", "one", "first");
        await CommitAsync("two.txt", "two", "second");

        var result = await _git.RewriteCommitMessageAsync(first, "修正後の件名\n\n修正後の本文");

        Assert.True(result.Success, result.Error);
        var messages = await MustRunAsync("log", "--format=%B%x1e");
        var commits = messages.Output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        Assert.Equal(new[] { "second", "修正後の件名\n\n修正後の本文" }, commits);
    }

    [Fact]
    public async Task スカッシュは編集したメッセージをtodo内で適用する()
    {
        var first = await CommitAsync("one.txt", "one", "first");
        var second = await CommitAsync("two.txt", "two", "second");

        var result = await _git.SquashAsync(new[] { second, first }, "まとめた件名\n\nまとめた本文");

        Assert.True(result.Success, result.Error);
        var count = await MustRunAsync("rev-list", "--count", "HEAD");
        Assert.Equal("1", count.Output.Trim());
        Assert.Equal("まとめた件名\n\nまとめた本文", await _git.GetCommitMessageAsync("HEAD"));
    }

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
