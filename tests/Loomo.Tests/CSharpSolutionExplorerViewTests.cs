using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

[Collection(WpfViewTests.Name)]
public sealed class CSharpSolutionExplorerViewTests
{
    private readonly WpfViewHost _host;

    public CSharpSolutionExplorerViewTests(WpfViewHost host) => _host = host;

    [Fact]
    public void ビューはsolution_treeを実際のWPFへ描画しfile_itemを生成する()
    {
        _host.Run(() =>
        {
            var filePath = @"C:\work\App\Program.cs";
            var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
                new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem("Program.cs", filePath)], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready);
            using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
                new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
                    [project], ProjectLoadState.Ready)));
            var view = new CSharpSolutionExplorerView { DataContext = vm };
            var window = new Window
            {
                Width = 520,
                Height = 420,
                Content = view,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var tree = FindVisual<TreeView>(view);
                Assert.NotNull(tree);
                Assert.Equal("CSharpSolutionTree", AutomationProperties.GetAutomationId(tree));
                Assert.Equal("C#ソリューションツリー", AutomationProperties.GetName(tree));
                Assert.Single(tree!.Items);
                Assert.Contains("App", AllText(view));

                var fileItem = FindVisual<TreeViewItem>(view,
                    item => item.DataContext is CSharpSolutionNodeViewModel
                    {
                        Kind: CSharpSolutionNodeKind.File,
                        FullPath: var path,
                    } && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(fileItem);
                Assert.Equal("Program.cs", AutomationProperties.GetName(fileItem));
                var projectItem = FindVisual<TreeViewItem>(view,
                    item => item.DataContext is CSharpSolutionNodeViewModel
                    {
                        Kind: CSharpSolutionNodeKind.Project,
                    });
                Assert.NotNull(projectItem);
                Assert.Equal("App", ((CSharpSolutionNodeViewModel)projectItem.DataContext!).Name);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AutomationPeerはsolutionとfileの表示名を公開する()
    {
        _host.Run(() =>
        {
            var filePath = @"C:\work\App\Program.cs";
            var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
                new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem("Program.cs", filePath)], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready);
            using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
                new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
                    [project], ProjectLoadState.Ready)));
            var view = new CSharpSolutionExplorerView { DataContext = vm };
            var window = new Window
            {
                Width = 520,
                Height = 420,
                Content = view,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var tree = FindVisual<TreeView>(view);
                Assert.NotNull(tree);
                var peer = UIElementAutomationPeer.CreatePeerForElement(tree!);
                Assert.NotNull(peer);
                var solutionPeer = Assert.Single(peer!.GetChildren() ?? []);
                Assert.Equal("App", solutionPeer.GetName());

                var filePeer = FindPeer(solutionPeer, "Program.cs");
                Assert.NotNull(filePeer);
                Assert.Equal("Program.cs", filePeer!.GetName());

                var selection = filePeer.GetPattern(PatternInterface.SelectionItem)
                    as ISelectionItemProvider;
                Assert.NotNull(selection);
                selection!.Select();
                var fileNode = FindNode(vm.Nodes, "Program.cs");
                Assert.NotNull(fileNode);
                Assert.Same(fileNode, tree!.SelectedItem);

                var opened = new List<string>();
                vm.FileOpenRequested += (_, path) => opened.Add(path);
                var key = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!, 0,
                    System.Windows.Input.Key.Enter)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                };
                tree.RaiseEvent(key);
                Assert.Equal([filePath], opened);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Solutionの操作メニューはUIA識別子を公開しActionへ結線される()
    {
        _host.Run(() =>
        {
            var filePath = @"C:\work\Tests\FeatureTests.cs";
            var project = new ProjectModel("Feature.Tests", @"C:\work\Tests\Feature.Tests.csproj",
                @"C:\work\Tests", [], [
                    new TargetFrameworkModel("net10.0", [], "latest",
                        [new ProjectItem("FeatureTests.cs", filePath)], [], [], [])],
                "net10.0", true, ProjectLoadState.Ready);
            using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
                new SolutionModel(@"C:\work\Tests\Tests.sln", "Tests", @"C:\work\Tests",
                    [project], ProjectLoadState.Ready)));
            var view = new CSharpSolutionExplorerView { DataContext = vm };
            var window = new Window
            {
                Width = 520,
                Height = 420,
                Content = view,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var projectItem = FindVisual<TreeViewItem>(view,
                    item => item.DataContext is CSharpSolutionNodeViewModel
                    {
                        Kind: CSharpSolutionNodeKind.Project,
                    });
                Assert.NotNull(projectItem);

                // ContextMenuEventArgsのコンストラクターはWPF内部用なので、実際のWPFが
                // ContextMenuOpeningを発火する状態を、STAテストでは未初期化インスタンスで再現する。
                var contextMenuOpening = (System.Windows.Controls.ContextMenuEventArgs)
                    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                        typeof(System.Windows.Controls.ContextMenuEventArgs));
                contextMenuOpening.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                projectItem!.RaiseEvent(contextMenuOpening);

                var menu = Assert.IsType<ContextMenu>(projectItem.ContextMenu);
                Assert.Equal("CSharpSolutionActions", AutomationProperties.GetAutomationId(menu));
                var actions = menu.Items.OfType<MenuItem>().ToArray();
                Assert.Contains(actions, item =>
                    AutomationProperties.GetAutomationId(item) == "CSharpSolutionAction.Build");
                Assert.Contains(actions, item =>
                    AutomationProperties.GetAutomationId(item) == "CSharpSolutionAction.Test");
                Assert.Contains(actions, item =>
                    AutomationProperties.GetAutomationId(item) == "CSharpSolutionAction.Run");
                Assert.Contains(actions, item =>
                    AutomationProperties.GetAutomationId(item) == "CSharpSolutionAction.Debug");

                CSharpSolutionActionEventArgs? requested = null;
                vm.ActionRequested += (_, e) => requested = e;
                var build = Assert.Single(actions,
                    item => AutomationProperties.GetAutomationId(item) == "CSharpSolutionAction.Build");
                build.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.NotNull(requested);
                Assert.Equal(CSharpSolutionAction.Build, requested!.Action);
                Assert.Equal("Feature.Tests", requested.Node.Name);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static AutomationPeer? FindPeer(AutomationPeer parent, string name)
    {
        foreach (var child in parent.GetChildren() ?? [])
        {
            if (string.Equals(child.GetName(), name, StringComparison.Ordinal)) return child;
            if (FindPeer(child, name) is { } found) return found;
        }
        return null;
    }

    private static CSharpSolutionNodeViewModel? FindNode(
        IEnumerable<CSharpSolutionNodeViewModel> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Name, name, StringComparison.Ordinal)) return node;
            if (FindNode(node.Children, name) is { } found) return found;
        }
        return null;
    }

    private static string AllText(DependencyObject root)
        => string.Join("\n", FindVisuals<TextBlock>(root).Select(block => block.Text));

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisuals<T>(child)) yield return nested;
        }
    }

    private static T? FindVisual<T>(DependencyObject root,
        Func<T, bool>? predicate = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate is null || predicate(match))) return match;
            if (FindVisual<T>(child, predicate) is { } nested) return nested;
        }
        return null;
    }

    private sealed class FakeSolutionService(SolutionModel initial) : ISolutionModelService
    {
        public SolutionModel Current { get; private set; } = initial;
        public event EventHandler<SolutionModel>? Changed
        {
            add { }
            remove { }
        }

        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);
    }
}
