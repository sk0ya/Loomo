using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>分岐元とぶつかりそうなら先に知らせる（§24.40）。実 git（手元の版に合わせて新旧どちらかの merge-tree を通る）。</summary>
public sealed class GitConflictForecastTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-conflict-forecast-tests", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init", "-b", "main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
        await WriteAsync("a.txt", "1\n2\n3\n");
        await WriteAsync("b.txt", "x\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "init");
        await MustRunAsync("checkout", "-b", "feature");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private Task WriteAsync(string name, string text) => File.WriteAllTextAsync(Path.Combine(_root, name), text);

    [Fact]
    public async Task 同じ行を両方で変えていれば衝突するファイルとして出し_作業ツリーには触らない()
    {
        await WriteAsync("a.txt", "1\nFEATURE\n3\n");
        await WriteAsync("b.txt", "x\ny\n");
        await MustRunAsync("commit", "-am", "feature");
        await MustRunAsync("checkout", "main");
        await WriteAsync("a.txt", "1\nMAIN\n3\n");
        await MustRunAsync("commit", "-am", "main");
        await MustRunAsync("checkout", "feature");

        var forecast = await _git.ForecastConflictsAsync("main");

        Assert.Null(forecast.Error);
        Assert.Equal(new[] { "a.txt" }, forecast.Files);
        Assert.Equal("1\nFEATURE\n3\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal("", (await MustRunAsync("status", "--porcelain")).Output.Trim());
    }

    [Fact]
    public async Task 別の場所を変えていれば衝突しない()
    {
        await WriteAsync("b.txt", "x\ny\n");
        await MustRunAsync("commit", "-am", "feature");
        await MustRunAsync("checkout", "main");
        await WriteAsync("a.txt", "1\nMAIN\n3\n");
        await MustRunAsync("commit", "-am", "main");
        await MustRunAsync("checkout", "feature");

        var forecast = await _git.ForecastConflictsAsync("main");

        Assert.False(forecast.HasConflicts);
        Assert.Null(forecast.Error);
    }

    [Fact]
    public async Task 同じコミットなら前回の答えをそのまま返す()
    {
        await WriteAsync("b.txt", "x\ny\n");
        await MustRunAsync("commit", "-am", "feature");

        var first = await _git.ForecastConflictsAsync("main");
        var second = await _git.ForecastConflictsAsync("main");

        Assert.Same(first, second);
    }

    [Fact]
    public void 古い形式の出力から衝突したファイルだけを読む()
    {
        var output = string.Join("\n",
            "changed in both",
            "  base   100644 0123 src/my file.cs",
            "  our    100644 4567 src/my file.cs",
            "  their  100644 89ab src/my file.cs",
            "@@ -1,3 +1,7 @@",
            " 1",
            "+<<<<<<< .our",
            " MAIN",
            "+=======",
            "+FEAT",
            "+>>>>>>> .their",
            "changed in both",
            "  base   100644 0123 clean.txt",
            "  our    100644 4567 clean.txt",
            "  their  100644 89ab clean.txt",
            "@@ -1 +1,2 @@",
            " x",
            "+y",
            "merged",
            "  result 100644 cdef b.txt",
            "  our    100644 0000 b.txt",
            "@@ -1 +1,2 @@",
            " x",
            "+y");

        Assert.Equal(new[] { "src/my file.cs" }, GitMergeTreeParser.ParseLegacyConflicts(output));
    }

    [Fact]
    public void 新しい形式は1行目のtreeを飛ばし_重なりをまとめる()
        => Assert.Equal(new[] { "a.txt", "b.txt" },
            GitMergeTreeParser.ParseWriteTreeNames("0123abcd\na.txt\na.txt\nb.txt\n"));

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
