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
            ExpandAll(vm.Nodes);
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
            ExpandAll(vm.Nodes);
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
            ExpandAll(vm.Nodes);
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

    /// <summary>行の選択・ホバー・見出しのボタンをすべてパレット（Accent/AccentFg/SecondaryButton）から
    /// 描くこと。既定の TreeViewItem テンプレートのままだと選択行が SystemColors の青で塗られ、
    /// どのテーマへ切り替えてもそこだけ配色が追従しない（実際にそうなっていた）。</summary>
    [Fact]
    public void 選択行と見出しボタンはテーマのブラシで描かれる()
    {
        _host.Run(() =>
        {
            using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(SampleSolution()));
            var view = new CSharpSolutionExplorerView { DataContext = vm };
            var window = new Window { Width = 520, Height = 420, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                var secondary = Application.Current!.Resources["SecondaryButton"];
                foreach (var id in new[] { "CSharpSolutionBuild", "CSharpSolutionTest" })
                {
                    var button = FindVisual<Button>(view,
                        b => AutomationProperties.GetAutomationId(b) == id);
                    Assert.NotNull(button);
                    Assert.Same(secondary, button!.Style);
                }

                var projectItem = FindVisual<TreeViewItem>(view,
                    item => item.DataContext is CSharpSolutionNodeViewModel
                    {
                        Kind: CSharpSolutionNodeKind.Project,
                    });
                Assert.NotNull(projectItem);
                projectItem!.IsSelected = true;
                window.UpdateLayout();

                var row = (Border)projectItem.Template.FindName("Bd", projectItem);
                Assert.Same(Application.Current.Resources["Accent"], row.Background);
                Assert.Same(Application.Current.Resources["AccentFg"], projectItem.Foreground);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>見出しのトグルでツリー本体だけを畳み、ホストが高さを詰められるよう通知すること。</summary>
    [Fact]
    public void 見出しのトグルでツリー本体を畳み展開できる()
    {
        _host.Run(() =>
        {
            using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(SampleSolution()));
            var view = new CSharpSolutionExplorerView { DataContext = vm };
            var window = new Window { Width = 520, Height = 420, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                var changed = 0;
                view.SectionExpandedChanged += (_, _) => changed++;
                var toggle = FindVisual<Button>(view,
                    b => AutomationProperties.GetAutomationId(b) == "CSharpSolutionSectionToggle");
                Assert.NotNull(toggle);
                Assert.True(view.IsSectionExpanded);

                toggle!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.False(view.IsSectionExpanded);
                Assert.Equal(1, changed);
                var body = (Grid)view.FindName("SectionBody");
                Assert.Equal(Visibility.Collapsed, body.Visibility);
                // 畳んでもツリーだけが消え、見出し（ビルド/テスト）は残る。
                Assert.NotNull(FindVisual<Button>(view,
                    b => AutomationProperties.GetAutomationId(b) == "CSharpSolutionBuild"));

                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.True(view.IsSectionExpanded);
                Assert.Equal(2, changed);
                Assert.Equal(Visibility.Visible, body.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static SolutionModel SampleSolution()
    {
        var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
            new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Program.cs", @"C:\work\App\Program.cs")], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
            [project], ProjectLoadState.Ready);
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

    [Fact]
    public void 既定で開くのはソリューションとプロジェクトだけ()
    {
        // 全段展開に戻すと、ソリューション全ファイルぶんの TreeViewItem が実体化する。
        // この repo では13,684ノードで、WPF の視覚要素とバインディングだけで gen2 が 900MB に達し、
        // ブロッキング GC のたびに UI が10秒級で止まる（実測19.5秒／Windows が AppHang で落とす）。
        _host.Run(() =>
        {
            var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
                new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem("Program.cs", @"C:\work\App\Program.cs")], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready);
            using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
                new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
                    [project], ProjectLoadState.Ready)));

            var solution = Assert.Single(vm.Nodes);
            Assert.True(solution.IsExpanded);
            var projectNode = Assert.Single(solution.Children,
                node => node.Kind == CSharpSolutionNodeKind.Project);
            Assert.True(projectNode.IsExpanded);
            Assert.NotEmpty(projectNode.Children);
            Assert.All(projectNode.Children, child => Assert.False(child.IsExpanded));
        });
    }

    [Fact]
    public void 再読み込みしても開いていたノードは開いたまま()
    {
        // ノード VM は Apply のたびに作り直すので、引き継がないと構成切替や .csproj の保存で
        // ツリーが畳まれ、利用者の手元が飛ぶ（IsExpanded を利用者の状態にした以上、必須）。
        _host.Run(() =>
        {
            var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
                new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem("Program.cs", @"C:\work\App\Program.cs")], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready);
            var service = new FakeSolutionService(
                new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
                    [project], ProjectLoadState.Ready));
            using var vm = new CSharpSolutionExplorerViewModel(service);

            var projectNode = FindNode(vm.Nodes, "App.csproj") ?? FindNodeOfKind(vm.Nodes, CSharpSolutionNodeKind.Project);
            Assert.NotNull(projectNode);
            var framework = Assert.Single(projectNode!.Children,
                node => node.Kind == CSharpSolutionNodeKind.TargetFramework);
            Assert.False(framework.IsExpanded);
            framework.IsExpanded = true;

            service.RaiseChanged();

            var rebuilt = FindNodeOfKind(vm.Nodes, CSharpSolutionNodeKind.TargetFramework);
            Assert.NotNull(rebuilt);
            Assert.NotSame(framework, rebuilt);
            Assert.True(rebuilt!.IsExpanded);
        });
    }

    private static CSharpSolutionNodeViewModel? FindNodeOfKind(
        IEnumerable<CSharpSolutionNodeViewModel> nodes, CSharpSolutionNodeKind kind)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == kind) return node;
            if (FindNodeOfKind(node.Children, kind) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// 全段を開いた状態にする。既定ではソリューションとプロジェクトしか開かない
    /// （全ファイルぶんの TreeViewItem を実体化させないため。<see cref="CSharpSolutionNodeViewModel.IsExpanded"/>）
    /// ので、ファイル行の描画を見るテストは自分で開いてから View を作る。
    /// IsExpanded は変更通知を持たないため、<b>ビューを作る前に</b>設定する。
    /// </summary>
    private static void ExpandAll(IEnumerable<CSharpSolutionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            ExpandAll(node.Children);
        }
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
        public event EventHandler<SolutionModel>? Changed;

        /// <summary>構成切替や .csproj 保存で起きる再読み込みを再現する。</summary>
        public void RaiseChanged() => Changed?.Invoke(this, Current);

        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);
    }
}
