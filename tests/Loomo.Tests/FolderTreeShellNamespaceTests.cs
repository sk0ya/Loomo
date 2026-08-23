using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class FolderTreeShellNamespaceTests
{
    [Theory]
    [InlineData("ごみ箱", FolderTreeShellNamespaces.RecycleBinId)]
    [InlineData("Network", FolderTreeShellNamespaces.NetworkId)]
    [InlineData("ライブラリ", FolderTreeShellNamespaces.LibrariesId)]
    public void Address_normalizes_known_namespace_aliases(string input, string id)
    {
        Assert.True(FolderTreeAddressHistory.TryNormalizePath(input, null, out var path));
        Assert.Equal($"shell:::{{{id}}}", path);
        Assert.True(FolderTreeShellNamespaces.IsShellPath(path));
    }

    [Fact]
    public void Address_keeps_virtual_children_and_resolves_parent()
    {
        var root = $"shell:::{{{FolderTreeShellNamespaces.LibrariesId}}}";
        var child = root + "\\Music.library-ms";

        Assert.True(FolderTreeAddressHistory.TryNormalizePath(child, null, out var normalized));
        Assert.Equal(child, normalized);
        Assert.Equal(root, FolderTreeShellNamespaces.Parent(normalized));
        Assert.Equal("Music.library-ms", FolderTreeShellNamespaces.Name(normalized));
    }

    [Fact]
    public void Address_resolves_relative_child_inside_virtual_namespace()
    {
        var root = $"shell:::{{{FolderTreeShellNamespaces.LibrariesId}}}";

        Assert.True(FolderTreeAddressHistory.TryNormalizePath("Music.library-ms", root, out var path));
        Assert.Equal(root + "\\Music.library-ms", path);
    }

    [Fact]
    public void Query_treats_virtual_paths_as_non_filesystem_paths_when_shell_is_unavailable()
    {
        var query = new FolderTreeQuery(new UnavailableShellProvider());
        var root = $"shell:::{{{FolderTreeShellNamespaces.NetworkId}}}";

        Assert.False(query.DirectoryExists(root));
        Assert.Empty(query.EnumerateChildren(root).Directories);
        Assert.Empty(query.EnumerateChildren(root).Files);
    }

    [Fact]
    public void Navigate_and_back_stay_inside_folder_tree_for_virtual_children()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "loomo-shell-nav-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var shellRoot = $"shell:::{{{FolderTreeShellNamespaces.NetworkId}}}";
            var shellChild = shellRoot + "\\Server";
            var workspace = new FakeWorkspaceService();
            var provider = new AvailableShellProvider(shellRoot, shellChild);
            var sut = CreateSut(workspace, new FolderTreeQuery(provider));
            sut.LoadRoot(root);

            var requested = false;
            sut.AddressNavigationRequested += (_, _) => requested = true;

            Assert.True(sut.NavigateAddress("Network"));
            Assert.Equal(shellRoot, sut.CurrentRoot);
            Assert.False(requested);
            var node = Assert.Single(sut.Nodes);
            Assert.True(node.IsShellItem);
            Assert.False(node.IsPinnable);
            Assert.False(sut.CanPin(shellChild));
            Assert.False(sut.IsPinnedPath(shellChild));

            Assert.True(sut.NavigateAddress("Server"));
            Assert.Equal(shellChild, sut.CurrentRoot);
            Assert.True(sut.CanGoBack);

            sut.GoBackCommand.Execute(null);
            Assert.Equal(shellRoot, sut.CurrentRoot);
            Assert.True(sut.CanGoForward);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Saved_virtual_child_root_is_restored_without_workspace_switch()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "loomo-shell-restore-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var shellRoot = $"shell:::{{{FolderTreeShellNamespaces.LibrariesId}}}";
            var shellChild = shellRoot + "\\Music.library-ms";
            var workspace = new FakeWorkspaceService();
            var provider = new AvailableShellProvider(shellRoot, shellChild);
            var sut = CreateSut(workspace, new FolderTreeQuery(provider));

            sut.LoadRoot(root, treeRootPath: shellChild);

            Assert.Equal(Path.GetFullPath(root), workspace.PrimaryFolder);
            Assert.Equal(shellChild, sut.CurrentRoot);
            Assert.Empty(sut.PinnedFolders);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void File_operations_reject_virtual_paths_before_file_command_layer()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "loomo-shell-ops-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var workspace = new FakeWorkspaceService();
            var sut = CreateSut(workspace, new FolderTreeQuery(new UnavailableShellProvider()));
            sut.LoadRoot(root);
            var shell = $"shell:::{{{FolderTreeShellNamespaces.RecycleBinId}}}";

            Assert.Throws<InvalidOperationException>(() => sut.CreateEntry(shell, "x.txt", false));
            Assert.Throws<InvalidOperationException>(() => sut.PasteEntry(root, shell, move: false));
            Assert.Throws<InvalidOperationException>(() => sut.PasteEntry(shell, root, move: false));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Unavailable_virtual_root_reports_shell_specific_empty_state()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "loomo-shell-empty-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var sut = CreateSut(new FakeWorkspaceService(), new FolderTreeQuery(new UnavailableShellProvider()));
            sut.LoadRoot(root);
            sut.SelectedRootOption = sut.RootOptions.Single(o =>
                o.IsShellNamespace && o.FullPath == FolderTreeShellNamespaces.Known[0].Path);

            Assert.Contains("Shell", sut.EmptyMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("利用できません", sut.EmptyMessage);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void LoadRoot_exposes_known_shell_roots_without_making_them_pinned()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "loomo-shell-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var workspace = new FakeWorkspaceService();
            var sut = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
                new sk0ya.Loomo.Core.Agent.WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-shell-workflows")),
                new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery(new UnavailableShellProvider()));

            sut.LoadRoot(root);

            Assert.Contains(sut.RootOptions, o => o.IsShellNamespace && !o.IsPinned);
            Assert.DoesNotContain(sut.PinnedFolders, p => FolderTreeShellNamespaces.IsShellPath(p));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class UnavailableShellProvider : WindowsFolderTreeShellNamespaceProvider
    {
        // Query は provider の concrete type を受けるため、存在確認だけを無効化する。
        public override bool Exists(string path) => false;
        public override FolderTreeEntries Enumerate(string path)
            => new(Array.Empty<string>(), Array.Empty<string>());
    }

    private sealed class AvailableShellProvider : WindowsFolderTreeShellNamespaceProvider
    {
        private readonly string _root;
        private readonly string _child;

        public AvailableShellProvider(string root, string child)
        {
            _root = root;
            _child = child;
        }

        public override bool Exists(string path)
            => FolderTreeShellNamespaces.IsShellPath(path)
               && (string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(path, _child, StringComparison.OrdinalIgnoreCase));

        public override FolderTreeEntries Enumerate(string path)
            => string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)
                ? new(new[] { _child }, Array.Empty<string>())
                : new(Array.Empty<string>(), Array.Empty<string>());
    }

    private static FolderTreeViewModel CreateSut(FakeWorkspaceService workspace, FolderTreeQuery query)
        => new(workspace, new FakeAiWarmup(),
            new sk0ya.Loomo.Core.Agent.WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-shell-workflows")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), query);
}
