using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>変更一覧の各行に増減の行数を添える（§24.32）。</summary>
public sealed class GitLineStatTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-line-stat-tests", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void 名前の変更は新しいパスに付き_バイナリは行数を持たない()
    {
        var output = "3\t1\tsrc/a.cs\0" + "2\t0\t\0old name.txt\0new name.txt\0" + "-\t-\timg.png\0";

        var stats = GitNumstatParser.Parse(output);

        Assert.Equal(new GitLineStat(3, 1, false), stats["src/a.cs"]);
        Assert.Equal(new GitLineStat(2, 0, false), stats["new name.txt"]);
        Assert.False(stats.ContainsKey("old name.txt"));
        Assert.True(stats["img.png"].IsBinary);
        Assert.Equal("bin", stats["img.png"].DisplayText);
        Assert.Equal("+3 −1", stats["src/a.cs"].DisplayText);
    }

    [Fact]
    public async Task ステージ済みと未ステージをそれぞれの側で数える()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "日本語 のファイル.txt"), "1\n2\n3\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "x\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "init");

        // ステージ済み：1 行足して 1 行消す。その上から未ステージで 2 行足す。
        await File.WriteAllTextAsync(Path.Combine(_root, "日本語 のファイル.txt"), "1\n2\nthree\n");
        await MustRunAsync("add", "日本語 のファイル.txt");
        await File.WriteAllTextAsync(Path.Combine(_root, "日本語 のファイル.txt"), "1\n2\nthree\n4\n5\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "untracked.txt"), "new\n");

        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace,
            new GitRootSwitchViewModel(_git, _workspace), new GitCompareBaseViewModel(_git));
        await vm.RefreshCommand.ExecuteAsync(null);

        var staged = Assert.Single(vm.Staged);
        Assert.Equal(new GitLineStat(1, 1, false), staged.LineStat);
        var unstaged = Assert.Single(vm.Changes);
        Assert.Equal(new GitLineStat(2, 0, false), unstaged.LineStat);
        // 未追跡のファイルは数えない（行には何も出さない）。
        Assert.Null(Assert.Single(vm.UnversionedFiles).LineStat);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
