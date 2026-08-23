using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

public sealed class FolderTreeQuickAccessTests : IDisposable
{
    private readonly string _root;
    private readonly string _folderA;
    private readonly string _folderB;
    private readonly FakeWorkspaceService _workspace = new();

    public FolderTreeQuickAccessTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"loomo-quick-access-{Guid.NewGuid():N}")).FullName;
        _folderA = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        _folderB = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");
        _workspace.OpenFolder(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Pin_menu_state_uses_Explorer_state_and_ignores_files_and_virtual_items()
    {
        var shell = new FakeQuickAccessService { Available = true };
        var sut = CreateViewModel(shell);
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var folder = sut.Nodes.Single(node => node.FullPath == _folderA);
        var file = sut.Nodes.Single(node => node.Name == "file.txt");
        var virtualNode = new FileNodeViewModel(
            $"shell:::{{{FolderTreeShellNamespaces.NetworkId}}}\\share",
            true,
            sut,
            _root,
            isShellItem: true);

        Assert.True(sut.CanPinToQuickAccess(new[] { folder, file, virtualNode }));
        shell.Pinned.Add(_folderA);
        Assert.False(sut.CanPinToQuickAccess(new[] { folder }));
        Assert.True(sut.CanUnpinFromQuickAccess(new[] { folder, file }));

        var result = sut.PinToQuickAccess(new[] { file, virtualNode });
        Assert.Equal(0, result.SucceededCount);
        Assert.Empty(result.FailedPaths);
        Assert.Equal(new[] { _folderA }, shell.Pinned);
    }

    [Fact]
    public async Task Multiple_selection_pins_only_directories_and_preserves_failure_paths()
    {
        var shell = new FakeQuickAccessService { Available = true };
        shell.FailPin.Add(_folderB);
        var sut = CreateViewModel(shell);
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var nodes = new[]
        {
            sut.Nodes.Single(node => node.FullPath == _folderA),
            sut.Nodes.Single(node => node.FullPath == _folderB),
            sut.Nodes.Single(node => node.Name == "file.txt"),
        };

        var result = sut.PinToQuickAccess(nodes);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(new[] { _folderB }, result.FailedPaths);
        Assert.Contains(_folderA, shell.Pinned);
        Assert.DoesNotContain(_root, shell.Pinned);
    }

    [Fact]
    public async Task Unpin_many_uses_current_state_and_does_not_touch_unpinned_folder()
    {
        var shell = new FakeQuickAccessService { Available = true };
        shell.Pinned.Add(_folderA);
        var sut = CreateViewModel(shell);
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var nodes = new[]
        {
            sut.Nodes.Single(node => node.FullPath == _folderA),
            sut.Nodes.Single(node => node.FullPath == _folderB),
        };

        var result = sut.UnpinFromQuickAccess(nodes);

        Assert.Equal(1, result.SucceededCount);
        Assert.Empty(result.FailedPaths);
        Assert.Empty(shell.Pinned);
        Assert.Equal(new[] { _folderA }, shell.Unpinned);
    }

    [Fact]
    public void Service_rejects_missing_and_virtual_paths_without_shell_side_effects()
    {
        var shell = new WindowsQuickAccessService();
        var missing = Path.Combine(_root, "missing");
        var virtualPath = $"shell:::{{{FolderTreeShellNamespaces.NetworkId}}}";

        Assert.False(shell.CanPin(missing));
        Assert.False(shell.IsPinned(virtualPath));
        Assert.Equal(QuickAccessOperationStatus.Unsupported, shell.Pin(missing).Status);
        Assert.Equal(QuickAccessOperationStatus.Unsupported, shell.Unpin(virtualPath).Status);
    }

    private FolderTreeViewModel CreateViewModel(IQuickAccessService quickAccess)
        => new(_workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-quick-access-workflows")),
            new FolderTreeCommandHandler(_workspace, new FileOperationHistory()),
            new FolderTreeQuery(), quickAccess: quickAccess);

    private sealed class FakeQuickAccessService : IQuickAccessService
    {
        public bool Available { get; set; }
        public HashSet<string> Pinned { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailPin { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Unpinned { get; } = new();
        public bool IsAvailable => Available;

        public bool IsPinned(string path) => Pinned.Contains(path);
        public bool CanPin(string path) => Available && Directory.Exists(path) && !IsPinned(path);

        public QuickAccessOperationResult Pin(string path)
        {
            if (!CanPin(path))
                return new(QuickAccessOperationStatus.AlreadyInRequestedState);
            if (FailPin.Contains(path))
                return new(QuickAccessOperationStatus.Failed, "fake failure");
            Pinned.Add(path);
            return new(QuickAccessOperationStatus.Succeeded);
        }

        public QuickAccessOperationResult Unpin(string path)
        {
            if (!Pinned.Remove(path))
                return new(QuickAccessOperationStatus.AlreadyInRequestedState);
            Unpinned.Add(path);
            return new(QuickAccessOperationStatus.Succeeded);
        }

        public QuickAccessBatchResult PinMany(IEnumerable<string> paths)
            => Apply(paths, Pin);

        public QuickAccessBatchResult UnpinMany(IEnumerable<string> paths)
            => Apply(paths, Unpin);

        public void Invalidate() { }

        private static QuickAccessBatchResult Apply(
            IEnumerable<string> paths,
            Func<string, QuickAccessOperationResult> action)
        {
            var succeeded = 0;
            var failed = new List<string>();
            foreach (var path in paths)
            {
                var result = action(path);
                if (result.Succeeded) succeeded++;
                else failed.Add(path);
            }
            return new(succeeded, failed, failed.Count == 0 ? null : "fake failure");
        }
    }
}
