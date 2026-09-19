using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペイン左列のブランチ絞り込み。<b>ブランチ切替ポップアップの絞り込みとは別の状態</b>で
/// なければならない——ポップアップは開くたびに自分の語を空へ戻すので、状態を共有すると
/// 「切り替えようとポップアップを開いたらペインの絞り込みが消えた」が起きる。
/// </summary>
[Collection(GitProcessTests.Name)]
public sealed class GitPaneBranchFilterTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-pane-filter", Guid.NewGuid().ToString("N"));
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
        await CommitAsync("a.txt", "a\n", "first");
        await MustRunAsync("branch", "feature/alpha");
        await MustRunAsync("branch", "feature/beta");
        await MustRunAsync("branch", "hotfix");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 語に一致したブランチだけが一覧に残る()
    {
        var vm = await LoadedVmAsync();

        vm.PaneBranchFilter = "beta";

        Assert.Equal(new[] { "feature/beta" }, Names(vm.PaneFilteredBranchTree));
        Assert.True(vm.HasPaneBranchFilter);
    }

    [Fact]
    public async Task 空語のときは元のツリーと同じインスタンスを返す()
    {
        var vm = await LoadedVmAsync();

        vm.PaneBranchFilter = "beta";
        vm.ClearPaneBranchFilterCommand.Execute(null);

        // 作り直すとフォルダの開閉も選択も飛ぶので、同一インスタンスで戻すこと。
        Assert.Same(vm.BranchTree, vm.PaneFilteredBranchTree);
        Assert.Equal("", vm.PaneBranchFilter);
        Assert.False(vm.HasPaneBranchFilter);
    }

    [Fact]
    public async Task ポップアップの絞り込みが空へ戻されてもペインの絞り込みは残る()
    {
        var vm = await LoadedVmAsync();
        vm.PaneBranchFilter = "feature";
        vm.BranchFilter = "hot";

        // BranchSwitcherView.PrepareForOpen（＝ポップアップを開くたび）と同じ操作。
        vm.BranchFilter = "";

        Assert.Equal("feature", vm.PaneBranchFilter);
        Assert.Equal(new[] { "feature/alpha", "feature/beta" }, Names(vm.PaneFilteredBranchTree));
        Assert.Same(vm.BranchTree, vm.FilteredBranchTree);
    }

    [Fact]
    public async Task 元ツリーと語が同じなら絞り込み結果を組み直さない()
    {
        // RepositoryChanged はファイルを保存しただけでも飛び、そのたびに RefreshAsync が
        // UpdatePaneFilteredBranchTree を呼ぶ。毎回新しいリストを入れると TreeView がコンテナごと
        // 作り直し、絞り込んで選んだ行の選択もフォーカスも（出かけていた操作メニューごと）落ちる。
        // 読み直しても BranchTree が同一インスタンスで返ること自体は BranchTreeBuilderTests の担当。
        var vm = await LoadedVmAsync();
        vm.PaneBranchFilter = "feature";
        var before = vm.PaneFilteredBranchTree;

        // 前後の空白は語としては同じ＝組み直す理由が無い。
        vm.PaneBranchFilter = " feature ";

        Assert.Same(before, vm.PaneFilteredBranchTree);
        Assert.Equal(new[] { "feature/alpha", "feature/beta" }, Names(vm.PaneFilteredBranchTree));
    }

    [Fact]
    public async Task 空白だけの語は絞り込み扱いにしない()
    {
        // 一覧は絞られないのに「✕」だけ出る＝効いていない絞り込みを効いているように見せる状態を作らない。
        var vm = await LoadedVmAsync();

        vm.PaneBranchFilter = "   ";

        Assert.False(vm.HasPaneBranchFilter);
        Assert.Same(vm.BranchTree, vm.PaneFilteredBranchTree);
    }

    [Theory]
    // 打った直後は VM がまだ空（Delay=120）でも、入力欄に字がある以上 Esc は消す側に効く。
    [InlineData(Key.Escape, "fea", true, BranchFilterKeyAction.Clear)]
    // 空での Esc は出口（キーボードで入ったのに出られない入力欄にしない）。
    [InlineData(Key.Escape, "", true, BranchFilterKeyAction.MoveToList)]
    [InlineData(Key.Down, "fea", true, BranchFilterKeyAction.MoveToList)]
    // 降りる先が無いときに握り潰すと、効かないキーを飲み込むだけになる。
    [InlineData(Key.Down, "zzz", false, BranchFilterKeyAction.None)]
    [InlineData(Key.A, "fea", true, BranchFilterKeyAction.None)]
    public void 絞り込み欄のキー割り当ては入力欄の文字で決まる(
        Key key, string text, bool hasRows, BranchFilterKeyAction expected)
    {
        Assert.Equal(expected, GitSessionView.ResolveBranchFilterKey(key, text, hasRows));
    }

    [Fact]
    public async Task 解除ボタンの出し入れは通知される()
    {
        var vm = await LoadedVmAsync();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PaneBranchFilter = "beta";

        Assert.Contains(nameof(GitSessionViewModel.HasPaneBranchFilter), changed);
    }

    private async Task<GitSessionViewModel> LoadedVmAsync()
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var git = new GitService(workspace);
        var query = new GitSessionQuery(git);
        var vm = new GitSessionViewModel(git, new FakeEditorService(), query,
            new GitSessionCommandHandler(git), new GitHistoryViewModel(query),
            new GitRootSwitchViewModel(git, workspace));
        vm.EnsureLoaded();
        await WaitAsync(() => vm.BranchTree.Count > 0);
        return vm;
    }

    /// <summary>ツリーのリーフ（＝ブランチ）のフルネームを並び順のまま。</summary>
    private static string[] Names(IReadOnlyList<BranchTreeNode> nodes) =>
        Flatten(nodes).Select(n => n.Branch!.Name).ToArray();

    private static IEnumerable<BranchTreeNode> Flatten(IReadOnlyList<BranchTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Branch is not null) yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        // 読み込みは fire-and-forget（EnsureLoaded）なので待つしかない。
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.True(condition(), $"条件が満たされませんでした（{elapsed.Elapsed.TotalSeconds:F1}秒待機）。");
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);
        await MustRunAsync("commit", "-m", message);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Error}");
        return result;
    }
}
