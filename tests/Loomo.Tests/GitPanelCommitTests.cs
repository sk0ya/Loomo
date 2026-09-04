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
    // 実アプリでは Singleton の1つを Git パネルと Diff ペインが共有する。テストの配線でも共有にしないと
    // 「どちらで切り替えても両方に効く」という中心的な不変条件が守られない。
    private GitCompareBaseViewModel _compareBase = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        _rootSwitch = new GitRootSwitchViewModel(_git, _workspace);
        _compareBase = new GitCompareBaseViewModel(_git);
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
        var vm = new GitPanelViewModel(_git, editor, _workspace, _rootSwitch, _compareBase);
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
        var vm = new GitPanelViewModel(_git, editor, _workspace, _rootSwitch, _compareBase);
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
    public async Task amend対象のコミットを履歴から選んで変更を追加できる()
    {
        await CommitFileAsync("first.txt", "first", "first commit");
        await CommitFileAsync("second.txt", "second", "second commit");

        await File.WriteAllTextAsync(Path.Combine(_root, "added-to-first.txt"), "added");
        await MustRunAsync("add", "added-to-first.txt");

        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace, _rootSwitch, _compareBase);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Amend = true;
        await vm.RefreshCommand.ExecuteAsync(null);

        var target = Assert.Single(vm.AmendCommits, c => c.Subject == "first commit");
        vm.SelectedAmendCommit = target;
        vm.CommitMessage = target.Subject;

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        var subjects = (await MustRunAsync("log", "--reverse", "--format=%s")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).ToArray();
        Assert.Equal(new[] { "first commit", "second commit" }, subjects);

        var firstHash = (await MustRunAsync("log", "--reverse", "--format=%H")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).First().Trim();
        var files = await MustRunAsync("show", "--pretty=format:", "--name-only", firstHash);
        Assert.Contains("added-to-first.txt", files.Output);
    }

    [Fact]
    public async Task amendが衝突したらリベースを中断して実行前の状態へ戻す()
    {
        await CommitFileAsync("conflict.txt", "one\n", "first commit");
        await CommitFileAsync("conflict.txt", "two\n", "second commit");
        var head = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();

        // 「first commit」へ入れようとするステージ済み変更。差分の文脈が second commit の内容なので、
        // autosquash の適用は必ず衝突する。
        await File.WriteAllTextAsync(Path.Combine(_root, "conflict.txt"), "three\n");
        await MustRunAsync("add", "conflict.txt");
        // 未ステージの変更（ここでは未追跡ファイル）も持たせ、退避したものが戻ることまで見る。
        await File.WriteAllTextAsync(Path.Combine(_root, "untracked.txt"), "keep me");

        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace, _rootSwitch, _compareBase);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Amend = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedAmendCommit = Assert.Single(vm.AmendCommits, c => c.Subject == "first commit");
        vm.CommitMessage = vm.SelectedAmendCommit.Subject;

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.StatusIsError);
        // 進行中のリベース・宙に浮いた fixup コミット・黙って作った stash を残さない。
        Assert.False(Directory.Exists(Path.Combine(_root, ".git", "rebase-merge")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".git", "rebase-apply")));
        Assert.Equal(head, (await MustRunAsync("rev-parse", "HEAD")).Output.Trim());
        Assert.Empty((await MustRunAsync("stash", "list")).Output.Trim());
        // ステージ済みの変更と未追跡ファイルは手元に残る。
        Assert.Contains("conflict.txt", (await MustRunAsync("diff", "--cached", "--name-only")).Output);
        Assert.True(File.Exists(Path.Combine(_root, "untracked.txt")));
    }

    [Fact]
    public async Task 同じ件名のコミットが並んでいても選んだコミットへamendする()
    {
        // --fixup=<sha> は「fixup! <件名>」を書くので、件名が重なると別のコミットへ吸い込まれる。
        await CommitFileAsync("a.txt", "a", "WIP");
        await CommitFileAsync("b.txt", "b", "WIP");
        // --max-count は --reverse より先に効く（HEAD 側が残る）ので、全部出してから先頭を取る。
        var older = (await MustRunAsync("rev-list", "--reverse", "HEAD")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).First().Trim();

        await File.WriteAllTextAsync(Path.Combine(_root, "added.txt"), "added");
        await MustRunAsync("add", "added.txt");

        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace, _rootSwitch, _compareBase);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Amend = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedAmendCommit = vm.AmendCommits.Single(c => c.Hash == older);
        vm.CommitMessage = vm.SelectedAmendCommit.Subject;

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        var hashes = (await MustRunAsync("rev-list", "--reverse", "HEAD")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).ToArray();
        Assert.Equal(2, hashes.Length);
        var files = await MustRunAsync("show", "--pretty=format:", "--name-only", hashes[0]);
        Assert.Contains("added.txt", files.Output);
    }

    [Fact]
    public async Task コミット可能な条件に応じてコマンドの有効状態が変わる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "candidate.txt"), "candidate");
        var editor = new FakeEditorService();
        var vm = new GitPanelViewModel(_git, editor, _workspace, _rootSwitch, _compareBase);
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
        // amend は対象コミットの選択が必須。まだ履歴がないため実行不可。
        Assert.False(vm.CommitCommand.CanExecute(null));
        vm.IsBusy = true;
        Assert.False(vm.CommitCommand.CanExecute(null));
    }

    [Fact]
    public void リモート同期コマンドは実行対象があるときだけ有効になる()
    {
        var editor = new FakeEditorService();
        var vm = new GitPanelViewModel(_git, editor, _workspace, _rootSwitch, _compareBase);

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

    [Fact]
    public void 差分を開くは表示要求を投げるだけでペインの状態には触らない()
    {
        // 出し先（Diff ペイン／ペインが隠れていれば別ウィンドウ）を決めるのは ShellWindow なので、
        // パネルが渡すのは「作業ツリーのこのファイル」という要求だけ。
        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace, _rootSwitch, _compareBase);
        var item = new GitChangeItem(new GitChangeEntry("src/a.cs", null, 'M', '.', false, false), isStaged: true);
        DiffOpenTarget? received = null;
        vm.DiffOpenRequested += (_, t) => received = t;

        vm.OpenDiffCommand.Execute(item);

        var target = Assert.IsType<DiffOpenTarget.WorkingTreeFile>(received);
        Assert.Equal("src/a.cs", target.Entry.Path);
        Assert.True(target.IsStaged);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }

    private async Task CommitFileAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);
        await MustRunAsync("commit", "-m", message);
    }
}
