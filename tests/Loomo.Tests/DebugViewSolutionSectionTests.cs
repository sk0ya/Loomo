using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

/// <summary>C# ソリューションツリーはサイドバーではなく IDE ペイン（実行タブの左列）に住む。
/// 実際の WPF へ描いて、段が出ていること・見出しのトグルで行が畳まれること・C# が無い
/// ワークスペースでは段ごと消えることを押さえる（XAML の文字列契約だけでは配置は守れない）。</summary>
[Collection(WpfViewTests.Name)]
public sealed class DebugViewSolutionSectionTests
{
    private readonly WpfViewHost _host;

    public DebugViewSolutionSectionTests(WpfViewHost host) => _host = host;

    [Fact]
    public void IDEペインの実行タブがソリューションツリーを表示し畳める()
    {
        _host.Run(() =>
        {
            using var solutionVm = new CSharpSolutionExplorerViewModel(new FakeSolutionModelService(Sample()));
            using var debugVm = CreateDebugViewModel(solutionVm);
            var view = new DebugView { DataContext = debugVm };
            var window = new Window { Width = 900, Height = 600, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                var section = (CSharpSolutionExplorerView)view.FindName("SolutionSection");
                Assert.Same(solutionVm, section.DataContext);
                Assert.Equal(Visibility.Visible, section.Visibility);
                var tree = FindVisual<TreeView>(section);
                Assert.NotNull(tree);
                Assert.Equal("CSharpSolutionTree", AutomationProperties.GetAutomationId(tree));

                var row = (RowDefinition)view.FindName("SolutionSectionRow");
                var splitter = (GridSplitter)view.FindName("SolutionSplitter");
                Assert.False(row.Height.IsAuto);
                Assert.True(row.Height.Value > 0);
                Assert.Equal(Visibility.Visible, splitter.Visibility);

                // 畳む：行は Auto（見出しだけ）へ落ち、境目のスプリッターも消える。
                section.SetSectionExpanded(false);
                window.UpdateLayout();
                Assert.True(row.Height.IsAuto);
                Assert.Equal(Visibility.Collapsed, splitter.Visibility);

                section.SetSectionExpanded(true);
                window.UpdateLayout();
                Assert.False(row.Height.IsAuto);
                Assert.Equal(Visibility.Visible, splitter.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CSharpのないワークスペースではソリューション段ごと消える()
    {
        _host.Run(() =>
        {
            using var solutionVm = new CSharpSolutionExplorerViewModel(new FakeSolutionModelService(
                new SolutionModel(null, "work", @"C:\work", [], ProjectLoadState.NotConfigured)));
            using var debugVm = CreateDebugViewModel(solutionVm);
            var view = new DebugView { DataContext = debugVm };
            var window = new Window { Width = 900, Height = 600, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.False(solutionVm.IsVisible);
                var row = (RowDefinition)view.FindName("SolutionSectionRow");
                var splitter = (GridSplitter)view.FindName("SolutionSplitter");
                Assert.Equal(0, row.Height.Value);
                Assert.Equal(Visibility.Collapsed, splitter.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>スプリッターで変えた段の高さは、畳んで展開しても既定値へ戻らないこと。
    /// ドラッグ後の行は Height に px が入るとは限らないので、実測値を覚える必要がある。</summary>
    [Fact]
    public void ドラッグで変えたソリューション段の高さは畳んで戻しても保たれる()
    {
        _host.Run(() =>
        {
            using var solutionVm = new CSharpSolutionExplorerViewModel(new FakeSolutionModelService(Sample()));
            using var debugVm = CreateDebugViewModel(solutionVm);
            var view = new DebugView { DataContext = debugVm };
            var window = new Window { Width = 900, Height = 600, Content = view, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                var section = (CSharpSolutionExplorerView)view.FindName("SolutionSection");
                var row = (RowDefinition)view.FindName("SolutionSectionRow");

                // スプリッターでのドラッグ相当（* へ変わることもあるので星で置く）。
                row.Height = new GridLength(1, GridUnitType.Star);
                window.UpdateLayout();
                var dragged = row.ActualHeight;
                Assert.True(dragged > 40, $"前提: 段が見えていること（実測 {dragged}）");

                section.SetSectionExpanded(false);
                window.UpdateLayout();
                section.SetSectionExpanded(true);
                window.UpdateLayout();

                Assert.Equal(dragged, row.Height.Value, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static DebugViewModel CreateDebugViewModel(CSharpSolutionExplorerViewModel solutionExplorer)
        => new(new sk0ya.Loomo.Services.Debug.NetcoredbgDebugSessionFactory(),
            new FakeWorkspaceService(), new FakeTerminalService(),
            new sk0ya.Loomo.CSharp.Testing.TestDiscoveryService(),
            new sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-launch-profiles.json")),
            solutionModel: null, browser: null, solutionExplorer: solutionExplorer);

    private static SolutionModel Sample()
    {
        var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
            new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Program.cs", @"C:\work\App\Program.cs")], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
            [project], ProjectLoadState.Ready);
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed class FakeSolutionModelService(SolutionModel initial) : ISolutionModelService
    {
        public SolutionModel Current { get; } = initial;
        public event EventHandler<SolutionModel>? Changed { add { } remove { } }

        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);
    }
}
