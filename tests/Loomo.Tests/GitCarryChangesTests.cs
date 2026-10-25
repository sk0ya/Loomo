using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>未コミットの変更を別のワークツリーへ写す／移す（§24.81）。実 git のワークツリーで確かめる。</summary>
public sealed class GitCarryChangesTests : IAsyncLifetime
{
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "loomo-git-carry", Guid.NewGuid().ToString("N"));
    private string _root = null!;
    private string _feature = null!;
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.txt"), "other\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
        _feature = GitWorktreeArgs.SuggestPath(_root, "feature");
        var added = await _git.AddWorktreeAsync(
            new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, _feature, "feature", "main"));
        Assert.True(added.Success, added.Message);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    private async Task<GitPanelViewModel> PanelAsync()
    {
        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace,
            new GitRootSwitchViewModel(_git, _workspace), new GitCompareBaseViewModel(_git))
        {
            WorktreesProvider = async () => await _git.GetWorktreesAsync(includeChangeCounts: false),
            ConfirmPatch = (_, _) => true,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task 運び先の候補にいまのワークツリーは入らない()
    {
        var vm = await PanelAsync();

        var target = Assert.Single(await vm.CarryTargetsAsync());
        Assert.Equal("feature", target.Branch);
    }

    [Fact]
    public async Task 移すと相手に入り_こちらからは選んだものだけ消える()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.txt"), "not picked\n");
        var vm = await PanelAsync();
        Assert.Single(vm.Changes, c => c.Entry.Path == "a.txt").IsChecked = true;
        Assert.Single(vm.UnversionedFiles).IsChecked = true;
        var target = Assert.Single(await vm.CarryTargetsAsync());

        await vm.CarryChangesAsync(vm.Changes.Single(c => c.Entry.Path == "a.txt"), target, move: true);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.Equal("a2\n", await File.ReadAllTextAsync(Path.Combine(_feature, "a.txt")));
        Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(_feature, "new.txt")));
        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "new.txt")));
        Assert.Equal("not picked\n", await File.ReadAllTextAsync(Path.Combine(_root, "other.txt")));
        Assert.Equal("other\n", await File.ReadAllTextAsync(Path.Combine(_feature, "other.txt")));
    }

    [Fact]
    public async Task ステージ済みの新しいファイルも移せてステージは残らない()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "staged.txt"), "staged\n");
        await MustRunAsync("add", "staged.txt");
        var vm = await PanelAsync();
        var target = Assert.Single(await vm.CarryTargetsAsync());
        var staged = Assert.Single(vm.Staged);

        await vm.CarryChangesAsync(staged, target, move: true);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.True(File.Exists(Path.Combine(_feature, "staged.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "staged.txt")));
        Assert.Equal("", (await MustRunAsync("status", "--porcelain")).Output.Trim());
    }

    [Fact]
    public async Task 写すと両方に残る()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");
        var vm = await PanelAsync();
        var target = Assert.Single(await vm.CarryTargetsAsync());

        await vm.CarryChangesAsync(vm.Changes.Single(c => c.Entry.Path == "a.txt"), target, move: false);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.Equal("a2\n", await File.ReadAllTextAsync(Path.Combine(_feature, "a.txt")));
        Assert.Equal("a2\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task 相手で当たらなければ何もしない()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");
        // 相手も同じ行を別の中身で書き換えている（未コミット）——素の apply も 3-way も当たらない。
        await File.WriteAllTextAsync(Path.Combine(_feature, "a.txt"), "feature side\n");
        var vm = await PanelAsync();
        var target = Assert.Single(await vm.CarryTargetsAsync());

        await vm.CarryChangesAsync(vm.Changes.Single(c => c.Entry.Path == "a.txt"), target, move: true);

        Assert.True(vm.StatusIsError);
        Assert.Equal("a2\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal("feature side\n", await File.ReadAllTextAsync(Path.Combine(_feature, "a.txt")));
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
