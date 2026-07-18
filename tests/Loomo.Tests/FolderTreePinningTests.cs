using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// FolderTree のピン留めとルート切替（ComboBox 候補）の検証。
/// ワークスペースの確定（OpenFolder）はルートのまま、ツリーの表示だけが切り替わる。
/// </summary>
public sealed class FolderTreePinningTests : IDisposable
{
    private readonly string _root;
    private readonly string _sub;
    private readonly string _nested;

    public FolderTreePinningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"loomo-pin-{Guid.NewGuid():N}");
        _sub = Path.Combine(_root, "src");
        _nested = Path.Combine(_sub, "App");
        Directory.CreateDirectory(_nested);
        File.WriteAllText(Path.Combine(_root, "root.txt"), "");
        File.WriteAllText(Path.Combine(_nested, "inner.txt"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private FolderTreeViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        return new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace), new FolderTreeQuery());
    }

    [Fact]
    public void LoadRoot_puts_workspace_root_as_first_option()
    {
        var sut = CreateSut();
        sut.LoadRoot(_root);

        var option = Assert.Single(sut.RootOptions);
        Assert.False(option.IsPinned);
        Assert.Equal(Path.GetFullPath(_root), option.FullPath);
        Assert.Same(option, sut.SelectedRootOption);
        Assert.Null(sut.TreeRootOverride);
    }

    [Fact]
    public void PinFolder_adds_option_with_relative_label()
    {
        var sut = CreateSut();
        sut.LoadRoot(_root);

        sut.PinFolder(_nested);

        var pinned = Assert.Single(sut.RootOptions, o => o.IsPinned);
        Assert.Equal(Path.Combine("src", "App"), pinned.Label);
        Assert.Equal(Path.GetFullPath(_nested), Assert.Single(sut.PinnedFolders));
        Assert.True(sut.IsPinnedPath(_nested));
    }

    [Fact]
    public void Pinning_root_or_duplicate_is_ignored()
    {
        var sut = CreateSut();
        sut.LoadRoot(_root);

        sut.PinFolder(_root);
        sut.PinFolder(_sub);
        sut.PinFolder(_sub);

        Assert.Single(sut.RootOptions, o => o.IsPinned);
    }

    [Fact]
    public async Task Selecting_pinned_option_switches_displayed_tree()
    {
        var sut = CreateSut();
        sut.LoadRoot(_root);
        sut.PinFolder(_nested);

        sut.SelectedRootOption = sut.RootOptions.Single(o => o.IsPinned);
        await sut.WhenTreeLoadedAsync();   // ツリー投入は git 読込の後（バックグラウンド）

        Assert.Equal(Path.GetFullPath(_nested), sut.TreeRootOverride);
        Assert.Contains(sut.Nodes, n => n.Name == "inner.txt");
        Assert.DoesNotContain(sut.Nodes, n => n.Name == "root.txt");
    }

    [Fact]
    public async Task Unpinning_current_root_falls_back_to_workspace_root()
    {
        var sut = CreateSut();
        sut.LoadRoot(_root);
        sut.PinFolder(_nested);
        sut.SelectedRootOption = sut.RootOptions.Single(o => o.IsPinned);

        sut.UnpinFolder(_nested);
        await sut.WhenTreeLoadedAsync();   // フォールバック先ルートのツリー投入を待つ

        Assert.Null(sut.TreeRootOverride);
        Assert.Same(sut.RootOptions[0], sut.SelectedRootOption);
        Assert.Contains(sut.Nodes, n => n.Name == "root.txt");
        Assert.Empty(sut.PinnedFolders);
    }

    [Fact]
    public async Task LoadRoot_restores_pins_and_tree_root_and_drops_missing_pins()
    {
        var sut = CreateSut();
        var missing = Path.Combine(_root, "deleted");

        sut.LoadRoot(_root, new[] { _nested, missing }, treeRootPath: _nested);
        await sut.WhenTreeLoadedAsync();   // ツリー投入は git 読込の後（バックグラウンド）

        Assert.Single(sut.RootOptions, o => o.IsPinned);   // 消えたピンは捨てる
        Assert.Equal(Path.GetFullPath(_nested), sut.TreeRootOverride);
        Assert.Contains(sut.Nodes, n => n.Name == "inner.txt");
    }

    [Fact]
    public void RootStateChanged_fires_on_pin_unpin_and_switch_but_not_on_load()
    {
        var sut = CreateSut();
        var fired = 0;
        sut.RootStateChanged += (_, _) => fired++;

        sut.LoadRoot(_root, new[] { _sub }, treeRootPath: _sub);
        Assert.Equal(0, fired);   // 復元では保存イベントを出さない

        sut.PinFolder(_nested);
        Assert.Equal(1, fired);

        sut.SelectedRootOption = sut.RootOptions.Single(o => !o.IsPinned);
        Assert.Equal(2, fired);

        sut.UnpinFolder(_nested);
        Assert.Equal(3, fired);
    }
}
