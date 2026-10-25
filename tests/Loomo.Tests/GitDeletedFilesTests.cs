using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>消えたファイルを探して戻す（§24.94）。実 git。</summary>
public sealed class GitDeletedFilesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-deleted-files-tests", Guid.NewGuid().ToString("N"));
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
        await MustRunAsync("config", "core.autocrlf", "false");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await WriteAsync("src/old.cs", "class Old {}\n");
        await WriteAsync("keep.txt", "keep\n");
        await WriteAsync("moved.txt", "a\nb\nc\nd\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "init");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private Task WriteAsync(string name, string text) => File.WriteAllTextAsync(Path.Combine(_root, name), text);

    [Fact]
    public async Task 消したファイルを消したコミットつきで新しい順に出し_名前を変えただけのものは出さない()
    {
        await MustRunAsync("rm", "-q", "src/old.cs");
        await MustRunAsync("mv", "moved.txt", "renamed.txt");
        await MustRunAsync("commit", "-m", "remove old");
        await MustRunAsync("rm", "-q", "keep.txt");
        await MustRunAsync("commit", "-m", "remove keep");

        var deleted = await _git.GetDeletedFilesAsync();

        Assert.Equal(new[] { "keep.txt", "src/old.cs" }, deleted.Select(d => d.Path));
        Assert.Equal("remove keep", deleted[0].Subject);
        Assert.Equal("remove old", deleted[1].Subject);
        Assert.Equal("Loomo Test", deleted[1].Author);
        Assert.NotNull(deleted[1].Date);
    }

    [Fact]
    public async Task 消す直前の版を読めて_作業ツリーへ戻せる()
    {
        await MustRunAsync("rm", "-q", "src/old.cs");
        await MustRunAsync("commit", "-m", "remove old");
        var file = Assert.Single(await _git.GetDeletedFilesAsync());

        var before = await _git.GetFileAtRevisionAsync(file.RevisionBefore, file.Path);
        Assert.True(before.Success, before.Message);
        Assert.Equal("class Old {}\n", before.Output);

        var restored = await _git.RestoreFileAtRevisionAsync(file.RevisionBefore, file.Path);
        Assert.True(restored.Success, restored.Message);
        Assert.Equal("class Old {}\n", await File.ReadAllTextAsync(Path.Combine(_root, "src", "old.cs")));
    }

    [Fact]
    public async Task 作り直してまた消したなら最後の削除だけを出す()
    {
        await MustRunAsync("rm", "-q", "keep.txt");
        await MustRunAsync("commit", "-m", "first remove");
        await WriteAsync("keep.txt", "again\n");
        await MustRunAsync("add", "keep.txt");
        await MustRunAsync("commit", "-m", "re-add");
        await MustRunAsync("rm", "-q", "keep.txt");
        await MustRunAsync("commit", "-m", "second remove");

        var file = Assert.Single(await _git.GetDeletedFilesAsync());

        Assert.Equal("second remove", file.Subject);
        Assert.Equal("again\n", (await _git.GetFileAtRevisionAsync(file.RevisionBefore, file.Path)).Output);
    }

    [Fact]
    public void 引用符で囲まれたパスは外し_見出しの無い行は読まない()
    {
        var output = "stray.txt\n\u001eabc\u001fab\u001fme\u001f2026-10-26T10:00:00+09:00\u001fsubj\n\n\"weird\\\"name.txt\"\n";

        var file = Assert.Single(GitDeletedFilesParser.Parse(output));

        Assert.Equal("weird\"name.txt", file.Path);
        Assert.Equal("abc^", file.RevisionBefore);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
