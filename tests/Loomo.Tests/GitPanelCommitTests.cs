using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class GitPanelCommitTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-panel-tests", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;
    private GitRootSwitchViewModel _rootSwitch = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        _rootSwitch = new GitRootSwitchViewModel(_git, _workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task チェックした作業ツリーのファイルだけをステージしてコミットする()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "checked.txt"), "checked");
        await File.WriteAllTextAsync(Path.Combine(_root, "unchecked.txt"), "unchecked");

        var editor = new FakeEditorService();
        var files = new DiffFileGateway();
        var diff = new DiffSessionViewModel(_git, editor, _workspace, files,
            new DiffSessionQuery(_git), new DiffSessionCommandHandler(_git));
        var vm = new GitPanelViewModel(_git, editor, _workspace, diff, _rootSwitch);
        await vm.RefreshCommand.ExecuteAsync(null);
        // 未追跡ファイルは「バージョン管理外ファイル」セクションに並び、既定では未チェック。
        var section = Assert.Single(vm.WorkingTreeSections);
        Assert.All(section.Children, n => Assert.False(n.IsChecked));
        section.Children.Single(n => n.Change!.Entry.Path == "checked.txt").IsChecked = true;
        vm.CommitMessage = "checked only";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        var committed = await MustRunAsync("show", "--pretty=format:", "--name-only", "HEAD");
        Assert.Contains("checked.txt", committed.Output);
        Assert.DoesNotContain("unchecked.txt", committed.Output);
        var status = await _git.GetStatusAsync();
        Assert.Contains(status.Unstaged, e => e.Path == "unchecked.txt" && e.IsUntracked);
    }

    [Fact]
    public async Task ステージ済みはチェックなしでそのままコミットされる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "staged.txt"), "staged");
        await MustRunAsync("add", "-A");

        var editor = new FakeEditorService();
        var files = new DiffFileGateway();
        var diff = new DiffSessionViewModel(_git, editor, _workspace, files,
            new DiffSessionQuery(_git), new DiffSessionCommandHandler(_git));
        var vm = new GitPanelViewModel(_git, editor, _workspace, diff, _rootSwitch);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(vm.Staged, i => i.Entry.Path == "staged.txt");
        Assert.Empty(vm.WorkingTreeSections);
        vm.CommitMessage = "commit staged";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        var committed = await MustRunAsync("show", "--pretty=format:", "--name-only", "HEAD");
        Assert.Contains("staged.txt", committed.Output);
    }

    [Fact]
    public async Task コミット可能な条件に応じてコマンドの有効状態が変わる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "candidate.txt"), "candidate");
        var editor = new FakeEditorService();
        var files = new DiffFileGateway();
        var diff = new DiffSessionViewModel(_git, editor, _workspace, files,
            new DiffSessionQuery(_git), new DiffSessionCommandHandler(_git));
        var vm = new GitPanelViewModel(_git, editor, _workspace, diff, _rootSwitch);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.CommitCommand.CanExecute(null));
        vm.CommitMessage = "candidate";
        Assert.False(vm.CommitCommand.CanExecute(null));

        var item = Assert.Single(vm.UnversionedFiles);
        item.IsChecked = true;
        Assert.True(vm.CommitCommand.CanExecute(null));

        item.IsChecked = false;
        Assert.False(vm.CommitCommand.CanExecute(null));
        vm.Amend = true;
        Assert.True(vm.CommitCommand.CanExecute(null));
        vm.IsBusy = true;
        Assert.False(vm.CommitCommand.CanExecute(null));
    }

    [Fact]
    public void リモート同期コマンドは実行対象があるときだけ有効になる()
    {
        var editor = new FakeEditorService();
        var files = new DiffFileGateway();
        var diff = new DiffSessionViewModel(_git, editor, _workspace, files,
            new DiffSessionQuery(_git), new DiffSessionCommandHandler(_git));
        var vm = new GitPanelViewModel(_git, editor, _workspace, diff, _rootSwitch);

        Assert.False(vm.FetchCommand.CanExecute(null));
        Assert.False(vm.PullCommand.CanExecute(null));
        Assert.False(vm.PushCommand.CanExecute(null));

        vm.HasRemote = true;
        Assert.True(vm.FetchCommand.CanExecute(null));
        Assert.False(vm.PullCommand.CanExecute(null));
        Assert.False(vm.PushCommand.CanExecute(null));

        vm.HasUpstream = true;
        vm.Behind = 1;
        vm.Ahead = 1;
        Assert.True(vm.PullCommand.CanExecute(null));
        Assert.True(vm.PushCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.FetchCommand.CanExecute(null));
        Assert.False(vm.PullCommand.CanExecute(null));
        Assert.False(vm.PushCommand.CanExecute(null));
    }

    [Fact]
    public void ディレクトリのチェックは配下へ伝播し親は一部選択を表す()
    {
        var a = new GitChangeItem(new GitChangeEntry("src/a.cs", null, '.', 'M', false, false), false);
        var b = new GitChangeItem(new GitChangeEntry("src/b.cs", null, '.', 'M', false, false), false);
        var c = new GitChangeItem(new GitChangeEntry("docs/c.md", null, '.', 'M', false, false), false);
        var root = GitChangeTreeNode.Build(new[] { a, b, c });
        var src = root.Children.Single(n => n.Name == "src");
        var srcLeaf = src.Children.Single(n => n.Change == a);
        // 連動した子・親の IsChecked 通知がバインディングに届くこと（通知名の取り違え防止）。
        var notified = new List<string?>();
        srcLeaf.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        root.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        src.IsChecked = true;

        Assert.True(a.IsChecked);
        Assert.True(b.IsChecked);
        Assert.False(c.IsChecked);
        Assert.Null(root.IsChecked);
        Assert.Contains(nameof(GitChangeTreeNode.IsChecked), notified);

        root.IsChecked = true;
        Assert.All(new[] { a, b, c }, item => Assert.True(item.IsChecked));
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
