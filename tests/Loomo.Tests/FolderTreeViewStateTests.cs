using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// FolderTree の展開・選択状態の保存と復元。ツリー投入は git 読込の後（非同期）なので、
/// 復元値は LoadRoot の前に保留として渡し、投入時に適用される。
/// </summary>
public sealed class FolderTreeViewStateTests : IDisposable
{
    private readonly string _root;
    private readonly string _src;
    private readonly string _app;
    private readonly string _docs;
    private readonly string _inner;
    private readonly string _secondary;

    public FolderTreeViewStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"loomo-viewstate-{Guid.NewGuid():N}");
        _src = Path.Combine(_root, "src");
        _app = Path.Combine(_src, "App");
        _docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(_docs);
        _inner = Path.Combine(_app, "inner.txt");
        File.WriteAllText(_inner, "");
        File.WriteAllText(Path.Combine(_docs, "readme.md"), "");

        _secondary = Path.Combine(Path.GetTempPath(), $"loomo-viewstate-{Guid.NewGuid():N}-b");
        Directory.CreateDirectory(Path.Combine(_secondary, "lib"));
        File.WriteAllText(Path.Combine(_secondary, "lib", "b.txt"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        try { Directory.Delete(_secondary, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private static FolderTreeViewModel CreateSut()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        return new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
    }

    private static FileNodeViewModel Find(FolderTreeViewModel sut, string path)
    {
        static IEnumerable<FileNodeViewModel> Walk(IEnumerable<FileNodeViewModel> nodes)
            => nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));
        return Walk(sut.Nodes).Single(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Capture_reports_only_visibly_expanded_folders_and_the_selection()
    {
        var sut = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        Find(sut, _src).IsExpanded = true;
        Find(sut, _app).IsExpanded = true;
        Find(sut, _inner).IsSelected = true;
        Find(sut, _docs).IsExpanded = true;
        Find(sut, _docs).IsExpanded = false;

        Assert.Equal([_src, _app], sut.CaptureExpandedPaths(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_inner, sut.CaptureSelectedPath(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_state_is_applied_when_the_tree_is_populated()
    {
        var sut = CreateSut();
        FileNodeViewModel? restored = null;
        sut.SelectionRestored += (_, node) => restored = node;

        sut.SetPendingViewState([_src, _app], _inner);
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        Assert.True(Find(sut, _src).IsExpanded);
        Assert.True(Find(sut, _app).IsExpanded);
        Assert.False(Find(sut, _docs).IsExpanded);
        Assert.True(Find(sut, _inner).IsSelected);
        Assert.Same(Find(sut, _inner), restored);
    }

    /// <summary>適用は初回投入の1回だけ。監視更新のたびに再適用すると、利用者が畳んだ枝が開き直る。</summary>
    [Fact]
    public async Task Pending_state_is_consumed_so_a_refresh_keeps_user_collapses()
    {
        var sut = CreateSut();
        sut.SetPendingViewState([_src], null);
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        Find(sut, _src).IsExpanded = false;
        sut.RefreshCommand.Execute(null);
        await sut.WhenTreeLoadedAsync();

        Assert.False(Find(sut, _src).IsExpanded);
        Assert.Empty(sut.CaptureExpandedPaths());
    }

    /// <summary>投入前に保存が走っても（起動直後のワークスペース切替など）保留分を失わない。</summary>
    [Fact]
    public void Capture_before_population_keeps_the_pending_state()
    {
        var sut = CreateSut();
        sut.SetPendingViewState([_src], _inner);

        Assert.Equal([_src], sut.CaptureExpandedPaths());
        Assert.Equal(_inner, sut.CaptureSelectedPath());
    }

    /// <summary>ワークスペース外のピンを表示ルートにしていても、適用した保留は消費される
    /// （ワークスペースルート基準だけで消すと、更新のたびに畳んだ枝が開き直り選択も奪い返される）。</summary>
    [Fact]
    public async Task Pending_state_under_a_pinned_root_outside_the_workspace_is_consumed()
    {
        var sut = CreateSut();
        var lib = Path.Combine(_secondary, "lib");
        sut.SetPendingViewState([lib], null);
        sut.LoadRoot(_root, pinnedFolders: [_secondary], treeRootPath: _secondary);
        sut.RestoreAdditionalFolders([]);
        await sut.WhenTreeLoadedAsync();
        Assert.True(Find(sut, lib).IsExpanded);

        Find(sut, lib).IsExpanded = false;
        sut.RefreshCommand.Execute(null);
        await sut.WhenTreeLoadedAsync();

        Assert.False(Find(sut, lib).IsExpanded);
        Assert.Empty(sut.CaptureExpandedPaths());
    }

    /// <summary>復元時に存在しなかった追加フォルダーぶんの保留は捨てる（保存のたびに書き戻され続けない）。</summary>
    [Fact]
    public void Pending_state_for_a_folder_that_cannot_be_restored_is_dropped()
    {
        var sut = CreateSut();
        var missing = Path.Combine(Path.GetTempPath(), $"loomo-viewstate-missing-{Guid.NewGuid():N}");
        var missingSub = Path.Combine(missing, "sub");

        sut.SetPendingViewState([_src, missingSub], Path.Combine(missing, "x.txt"));
        sut.LoadRoot(_root);
        sut.RestoreAdditionalFolders([new WorkspaceFolderPin { FolderPath = missing }]);

        Assert.Equal([_src], sut.CaptureExpandedPaths());
        Assert.Null(sut.CaptureSelectedPath());
    }

    [Fact]
    public async Task Pending_state_is_applied_per_folder_in_a_multi_root_workspace()
    {
        var sut = CreateSut();
        var lib = Path.Combine(_secondary, "lib");
        var libFile = Path.Combine(lib, "b.txt");

        sut.SetPendingViewState([_src, lib], libFile);
        sut.LoadRoot(_root);
        sut.RestoreAdditionalFolders([new WorkspaceFolderPin { FolderPath = _secondary }]);
        await sut.WhenTreeLoadedAsync();

        Assert.True(Find(sut, _src).IsExpanded);
        Assert.True(Find(sut, lib).IsExpanded);
        Assert.True(Find(sut, libFile).IsSelected);
        // 見出しは自動展開なので保存対象外。
        Assert.Equal([_src, lib], sut.CaptureExpandedPaths(), StringComparer.OrdinalIgnoreCase);
    }
}
