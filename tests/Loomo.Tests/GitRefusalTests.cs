using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>git が止めた操作の読み分け（§24.99）。</summary>
public sealed class GitRefusalTests
{
    [Fact]
    public void リモートが進んでいて拒まれたpushだけを読む()
    {
        const string fetchFirst = """
            To github.com:me/app.git
             ! [rejected]        main -> main (fetch first)
            error: failed to push some refs to 'github.com:me/app.git'
            hint: Updates were rejected because the remote contains work that you do not
            """;
        const string nonFastForward = " ! [rejected]        main -> main (non-fast-forward)\n";
        const string stale = " ! [rejected]        main -> main (stale info)\n";
        Assert.True(GitRefusal.IsPushBehind(fetchFirst));
        Assert.True(GitRefusal.IsPushBehind(nonFastForward));
        // lease の拒否は取り込んでも直らないので別物。
        Assert.False(GitRefusal.IsPushBehind(stale));
        Assert.False(GitRefusal.IsPushBehind("fatal: Authentication failed"));
        Assert.False(GitRefusal.IsPushBehind(null));
    }

    [Fact]
    public void 切替を止めたファイルを一覧の順に読む()
    {
        const string error = "error: Your local changes to the following files would be overwritten by checkout:\n" +
                             "\tsrc/a.cs\n\tREADME.md\n" +
                             "Please commit your changes or stash them before you switch branches.\nAborting\n";
        var files = GitRefusal.CheckoutBlockingFiles(error, out var untracked);
        Assert.Equal(["src/a.cs", "README.md"], files);
        Assert.False(untracked);

        const string untrackedError = "error: The following untracked working tree files would be overwritten by checkout:\n" +
                                      "\tnew.txt\nPlease move or remove them before you switch branches.\nAborting\n";
        Assert.Equal(["new.txt"], GitRefusal.CheckoutBlockingFiles(untrackedError, out untracked));
        Assert.True(untracked);

        Assert.Null(GitRefusal.CheckoutBlockingFiles("error: pathspec 'nope' did not match", out _));
    }
}

/// <summary>実際の git で、ローカル変更を退避して・持って切り替える（§24.99）。</summary>
public sealed class GitCheckoutRefusalServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-refusal", Guid.NewGuid().ToString("N"));
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
        await MustRunAsync("config", "core.autocrlf", "false");
        await CommitAsync("a.txt", "base\n", "base");
        await MustRunAsync("branch", "feature");
        await CommitAsync("a.txt", "main\n", "main side");
        await MustRunAsync("checkout", "-q", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "local edit\n");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 止まった切替を退避してやり直すと変更はスタッシュに残る()
    {
        var refused = await _git.CheckoutAsync("main");
        Assert.False(refused.Success);
        Assert.Equal(["a.txt"], GitRefusal.CheckoutBlockingFiles(refused.Error, out _));

        var result = await _git.CheckoutStashingAsync("main", track: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal("main", (await MustRunAsync("branch", "--show-current")).Output.Trim());
        Assert.Equal("main\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Contains(GitRefusal.CheckoutStashMessage("main"), (await MustRunAsync("stash", "list")).Output);
    }

    [Fact]
    public async Task 切替先が無ければ退避を戻して変更を置き去りにしない()
    {
        var result = await _git.CheckoutStashingAsync("no-such-branch", track: false);

        Assert.False(result.Success);
        Assert.Equal("local edit\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal("", (await MustRunAsync("stash", "list")).Output.Trim());
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-q", "-m", message);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
