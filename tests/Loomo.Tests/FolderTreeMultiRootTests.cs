using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 複数フォルダーワークスペース（マルチルート）でのフォルダーツリー表示の検証。
/// 単一フォルダー時の挙動（<see cref="FolderTreePinningTests"/>）は無退行であることが前提。
/// </summary>
public sealed class FolderTreeMultiRootTests : IDisposable
{
    private readonly string _primary;
    private readonly string _secondary;

    public FolderTreeMultiRootTests()
    {
        _primary = Path.Combine(Path.GetTempPath(), $"loomo-multiroot-{Guid.NewGuid():N}");
        _secondary = Path.Combine(Path.GetTempPath(), $"loomo-multiroot-{Guid.NewGuid():N}-b");
        Directory.CreateDirectory(_primary);
        Directory.CreateDirectory(_secondary);
        File.WriteAllText(Path.Combine(_primary, "primary.txt"), "");
        File.WriteAllText(Path.Combine(_secondary, "secondary.txt"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_primary, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        try { Directory.Delete(_secondary, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private (FolderTreeViewModel Sut, IWorkspaceService Workspace) CreateSut()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        var sut = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace), new FolderTreeQuery());
        return (sut, workspace);
    }

    [Fact]
    public async Task AddFolder_shows_a_header_node_per_workspace_folder()
    {
        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();

        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        Assert.True(sut.IsMultiRootWorkspace);
        Assert.Equal(2, sut.Nodes.Count);
        Assert.All(sut.Nodes, n => Assert.True(n.IsWorkspaceFolderRoot));
        Assert.Contains(sut.Nodes, n => n.RootKey == Path.GetFullPath(_primary));
        Assert.Contains(sut.Nodes, n => n.RootKey == Path.GetFullPath(_secondary));
    }

    [Fact]
    public async Task Each_header_lists_only_its_own_folder_contents()
    {
        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        var primaryHeader = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_primary));
        var secondaryHeader = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_secondary));

        Assert.Contains(primaryHeader.Children, c => c.Name == "primary.txt");
        Assert.DoesNotContain(primaryHeader.Children, c => c.Name == "secondary.txt");
        Assert.Contains(secondaryHeader.Children, c => c.Name == "secondary.txt");
        Assert.DoesNotContain(secondaryHeader.Children, c => c.Name == "primary.txt");
    }

    [Fact]
    public async Task RemoveFolder_back_to_one_restores_flat_single_root_view()
    {
        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        workspace.RemoveFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        Assert.False(sut.IsMultiRootWorkspace);
        Assert.Contains(sut.Nodes, n => n.Name == "primary.txt");
        Assert.DoesNotContain(sut.Nodes, n => n.IsWorkspaceFolderRoot);
    }

    [Fact]
    public async Task AddFolder_does_not_disturb_existing_header_identity()
    {
        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        var firstPrimaryHeader = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_primary));

        var third = Path.Combine(Path.GetTempPath(), $"loomo-multiroot-{Guid.NewGuid():N}-c");
        Directory.CreateDirectory(third);
        try
        {
            workspace.AddFolder(third);
            await sut.WhenTreeLoadedAsync();

            var laterPrimaryHeader = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_primary));
            Assert.Same(firstPrimaryHeader, laterPrimaryHeader);
            Assert.Equal(3, sut.Nodes.Count);
        }
        finally
        {
            try { Directory.Delete(third, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        }
    }

    [Fact]
    public async Task CaptureAdditionalFolders_and_RestoreAdditionalFolders_round_trip()
    {
        var nestedInSecondary = Path.Combine(_secondary, "nested");
        Directory.CreateDirectory(nestedInSecondary);

        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        sut.PinFolder(nestedInSecondary);
        var secondaryHeader = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_secondary));
        sut.SwitchRootOption(secondaryHeader,
            sut.RootOptionsFor(secondaryHeader).Single(o => o.IsPinned));
        await sut.WhenTreeLoadedAsync();

        var captured = sut.CaptureAdditionalFolders();
        var pin = Assert.Single(captured);
        Assert.Equal(Path.GetFullPath(_secondary), pin.FolderPath);
        Assert.Equal(Path.GetFullPath(nestedInSecondary), Assert.Single(pin.PinnedFolders));
        Assert.Equal(Path.GetFullPath(nestedInSecondary), pin.TreeRootPath);

        // 新しい FolderTreeViewModel（同じワークスペースフォルダー構成）へ復元する。
        var (restoredSut, restoredWorkspace) = CreateSut();
        restoredSut.LoadRoot(_primary);
        await restoredSut.WhenTreeLoadedAsync();
        restoredSut.RestoreAdditionalFolders(captured);
        await restoredSut.WhenTreeLoadedAsync();

        Assert.Equal(new[] { Path.GetFullPath(_primary), Path.GetFullPath(_secondary) },
            restoredWorkspace.Folders);
        var restoredHeader = restoredSut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_secondary));
        Assert.Equal(Path.GetFullPath(nestedInSecondary), restoredHeader.FullPath);
        Assert.True(restoredSut.IsPinnedPath(nestedInSecondary));
    }

    [Fact]
    public void RestoreAdditionalFolders_does_not_fire_RootStateChanged()
    {
        var (sut, _) = CreateSut();
        var fired = 0;
        sut.RootStateChanged += (_, _) => fired++;

        sut.LoadRoot(_primary);
        sut.RestoreAdditionalFolders(new[]
        {
            new sk0ya.Loomo.App.Services.WorkspaceFolderPin { FolderPath = _secondary }
        });

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task PinFolder_within_one_root_does_not_affect_sibling_root()
    {
        var nestedInPrimary = Path.Combine(_primary, "nested");
        Directory.CreateDirectory(nestedInPrimary);
        File.WriteAllText(Path.Combine(nestedInPrimary, "deep.txt"), "");

        var (sut, workspace) = CreateSut();
        sut.LoadRoot(_primary);
        await sut.WhenTreeLoadedAsync();
        workspace.AddFolder(_secondary);
        await sut.WhenTreeLoadedAsync();

        sut.PinFolder(nestedInPrimary);
        Assert.True(sut.IsPinnedPath(nestedInPrimary));
        Assert.False(sut.IsPinnedPath(_secondary));

        var secondaryHeaderBefore = sut.Nodes.Single(n => n.RootKey == Path.GetFullPath(_secondary));
        Assert.Contains(secondaryHeaderBefore.Children, c => c.Name == "secondary.txt");
    }
}
