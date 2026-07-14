using System.Diagnostics;
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

    [Fact]
    public async Task スカッシュの順序判定はコミット日時ではなく親子関係を使う()
    {
        var first = await CommitWithDateAsync("one.txt", "one", "first", "2030-01-01T00:00:00+00:00");
        var second = await CommitWithDateAsync("two.txt", "two", "second", "2020-01-01T00:00:00+00:00");

        var result = await _git.SquashAsync(new[] { first, second }, "まとめたコミット");

        Assert.True(result.Success, result.Error);
        Assert.Equal("1", (await MustRunAsync("rev-list", "--count", "HEAD")).Output.Trim());
        Assert.True(File.Exists(Path.Combine(_root, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "two.txt")));
    }

    [Fact]
    public async Task スカッシュ対象より後にマージがある場合は履歴を書き換えない()
    {
        var first = await CommitAsync("one.txt", "one", "first");
        var second = await CommitAsync("two.txt", "two", "second");
        var mainBranch = (await MustRunAsync("branch", "--show-current")).Output.Trim();
        await MustRunAsync("branch", "side");
        await CommitAsync("main.txt", "main", "main");
        await MustRunAsync("checkout", "side");
        await CommitAsync("side.txt", "side", "side");
        await MustRunAsync("checkout", mainBranch);
        await MustRunAsync("merge", "--no-ff", "-m", "merge", "side");
        var headBefore = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();

        var result = await _git.SquashAsync(new[] { first, second }, "まとめたコミット");

        Assert.False(result.Success);
        Assert.Contains("マージコミット", result.Error);
        Assert.Equal(headBefore, (await MustRunAsync("rev-parse", "HEAD")).Output.Trim());
    }

    private async Task<string> CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<string> CommitWithDateAsync(string path, string content, string message, string date)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("commit");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(message);
        startInfo.Environment["GIT_AUTHOR_DATE"] = date;
        startInfo.Environment["GIT_COMMITTER_DATE"] = date;

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{output}\n{error}");
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
