using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class FolderTreeAddressTests : IDisposable
{
    private readonly string _root;
    private readonly string _child;
    private readonly string _sibling;

    public FolderTreeAddressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"loomo-address-{Guid.NewGuid():N}");
        _child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        _sibling = Directory.CreateDirectory(Path.Combine(_root, "sibling")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    [Fact]
    public void NormalizePath_accepts_unc_paths_without_touching_the_share()
    {
        const string unc = @"\\server\share\folder";

        Assert.True(FolderTreeAddressHistory.TryNormalizePath(unc, null, out var fullPath));
        Assert.Equal(unc, fullPath);
    }

    [Fact]
    public void History_moves_duplicate_to_the_front_and_is_bounded()
    {
        var history = new FolderTreeAddressHistory(capacity: 2);
        history.Add(Path.Combine(_root, "one"));
        history.Add(Path.Combine(_root, "two"));
        history.Add(Path.Combine(_root, "one"));

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "one")), history.Entries[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "two")), history.Entries[1]);
    }

    [Fact]
    public void Suggestions_include_directories_after_a_trailing_separator()
    {
        var history = new FolderTreeAddressHistory();
        var suggestions = history.Suggest(_root + Path.DirectorySeparatorChar, _root);

        Assert.Contains(_child, suggestions);
        Assert.Contains(_sibling, suggestions);
    }

    [Fact]
    public async Task NavigateAddress_changes_display_root_without_changing_workspace_root()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        var sut = CreateSut(workspace);
        sut.LoadRoot(_root);
        Assert.Equal(Path.GetFullPath(_root), sut.AddressText);
        await sut.WhenTreeLoadedAsync();

        Assert.True(sut.NavigateAddress("child"));
        await sut.WhenTreeLoadedAsync();

        Assert.Equal(Path.GetFullPath(_root), workspace.PrimaryFolder);
        Assert.Equal(_child, sut.CurrentRoot);
        Assert.Equal(_child, sut.TreeRootOverride);
        Assert.Equal(_child, sut.AddressText);
        Assert.Contains(_child, sut.AddressHistory);
    }

    [Fact]
    public void NavigateAddress_rejects_missing_directories()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        var sut = CreateSut(workspace);
        sut.LoadRoot(_root);

        Assert.False(sut.NavigateAddress("does-not-exist"));
        Assert.True(sut.HasAddressError);
        Assert.Contains("存在しません", sut.AddressError);
    }

    [Fact]
    public void NavigateAddress_requests_workspace_switch_for_an_external_folder()
    {
        var external = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"loomo-address-external-{Guid.NewGuid():N}")).FullName;
        try
        {
            var workspace = new WorkspaceService(new SafetySettings());
            var sut = CreateSut(workspace);
            sut.LoadRoot(_root);
            string? requested = null;
            sut.AddressNavigationRequested += (_, path) => requested = path;

            Assert.True(sut.NavigateAddress(external));
            Assert.Equal(external, requested);
        }
        finally
        {
            try { Directory.Delete(external, recursive: true); } catch { }
        }
    }

    private static FolderTreeViewModel CreateSut(WorkspaceService workspace)
        => new(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());
}
