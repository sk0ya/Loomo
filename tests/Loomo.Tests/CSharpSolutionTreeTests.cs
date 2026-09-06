using System.Collections.Specialized;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpSolutionTreeTests
{
    [Fact]
    public void Uses_project_references_from_the_selected_target_framework()
    {
        var project = new ProjectModel(
            "App", @"C:\work\App\App.csproj", @"C:\work\App", [],
            [
                new TargetFrameworkModel("net8.0", [], "latest", [], [], [], [])
                {
                    ProjectReferences = [@"C:\work\Legacy\Legacy.csproj"],
                },
                new TargetFrameworkModel("net9.0", [], "latest", [], [], [], [])
                {
                    ProjectReferences = [@"C:\work\Modern\Modern.csproj"],
                },
            ], "net9.0", false, ProjectLoadState.Ready);

        var projectNode = Assert.Single(
            CSharpSolutionTreeBuilder.Build(new SolutionModel(
                @"C:\work\App.sln", "App", @"C:\work", [project], ProjectLoadState.Ready)).Children);
        var references = Assert.Single(projectNode.Children,
            node => node.Kind == CSharpSolutionNodeKind.ProjectReference);

        Assert.Equal("Modern", Assert.Single(references.Children).Name);
    }

    [Fact]
    public void Builds_solution_project_reference_tfm_folders_files_and_analyzers()
    {
        var root = new SolutionModel("C:\\work\\App.sln", "App", "C:\\work", [
            new ProjectModel("App", "C:\\work\\src\\App\\App.csproj", "C:\\work\\src\\App",
                ["C:\\work\\src\\Lib\\Lib.csproj"], [
                    new TargetFrameworkModel("net8.0", ["NET8_0"], "latest",
                        [new ProjectItem("Main.cs", "C:\\work\\src\\App\\Main.cs"),
                         new ProjectItem("Models\\User.cs", "C:\\work\\src\\App\\Models\\User.cs")],
                        [new ProjectItem("StyleCop.Analyzers", "C:\\packages\\stylecop.dll")], [],
                        [new ProjectItem("README.md", "C:\\work\\src\\App\\README.md")]),
                    new TargetFrameworkModel("net9.0", ["NET9_0"], "latest", [], [], [], [])],
                "net8.0", false, ProjectLoadState.Ready),
        ], ProjectLoadState.Ready);

        var tree = CSharpSolutionTreeBuilder.Build(root);
        var project = Assert.Single(tree.Children);
        Assert.Equal(CSharpSolutionNodeKind.Project, project.Kind);
        Assert.Contains(project.Children, n => n.Kind == CSharpSolutionNodeKind.ProjectReference);
        var selected = Assert.Single(project.Children,
            n => n.Kind == CSharpSolutionNodeKind.TargetFramework && n.IsSelected);
        Assert.Equal("net8.0", selected.Name);
        Assert.Contains(selected.Children, n => n.Name == "Main.cs" && n.Kind == CSharpSolutionNodeKind.File);
        var models = Assert.Single(selected.Children, n => n.Name == "Models");
        Assert.Contains(models.Children, n => n.Name == "User.cs");
        Assert.Contains(selected.Children, n => n.Kind == CSharpSolutionNodeKind.Analyzer);
        Assert.Contains(selected.Children, n => n.Name == "その他ファイル" && n.Kind == CSharpSolutionNodeKind.NoneFile);
    }

    [Fact]
    public void Preserves_link_path_for_files_outside_project_directory()
    {
        var project = new ProjectModel("App", "C:\\work\\App.csproj", "C:\\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem("..\\Shared\\Common.cs", "C:\\Shared\\Common.cs", "Shared\\Common.cs")
            ], [], [], [])], "net10.0", false, ProjectLoadState.Ready);
        var tree = CSharpSolutionTreeBuilder.Build(new SolutionModel(null, "App", "C:\\work", [project], ProjectLoadState.Ready));

        // TFMが1つなら段を挟まないので、プロジェクトの直下がそのままファイル階層になる。
        var projectNode = Assert.Single(tree.Children);
        var shared = Assert.Single(projectNode.Children, n => n.Name == "Shared");
        var common = Assert.Single(shared.Children);
        Assert.Equal("Common.cs", common.Name);
        Assert.Equal("C:\\Shared\\Common.cs", common.FullPath);
    }

    [Fact]
    public void Solution_explorer_viewmodel_exposes_file_open_requests()
    {
        var source = "C:\\work\\App.cs";
        var project = new ProjectModel("App", "C:\\work\\App.csproj", "C:\\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [new ProjectItem("App.cs", source)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(null, "App", "C:\\work", [project], ProjectLoadState.Ready));
        using var vm = new CSharpSolutionExplorerViewModel(service);
        string? opened = null;
        vm.FileOpenRequested += (_, path) => opened = path;

        var file = Find(vm.Nodes, n => n.Kind == CSharpSolutionNodeKind.File);
        vm.Open(file);

        Assert.True(vm.IsVisible);
        Assert.Equal(source, opened);
    }

    [Fact]
    public void Solution_explorer_exposes_build_and_test_actions_for_project_nodes()
    {
        var project = new ProjectModel("Tests", "C:\\work\\Tests.csproj", "C:\\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [], [], [], [])
        ], "net10.0", true, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(
            "C:\\work\\App.sln", "App", "C:\\work", [project], ProjectLoadState.Ready));
        using var vm = new CSharpSolutionExplorerViewModel(service);
        CSharpSolutionActionEventArgs? requested = null;
        vm.ActionRequested += (_, args) => requested = args;

        var projectNode = Find(vm.Nodes, n => n.Kind == CSharpSolutionNodeKind.Project);
        vm.RequestAction(projectNode, CSharpSolutionAction.Test);

        Assert.NotNull(requested);
        Assert.Equal(CSharpSolutionAction.Test, requested!.Action);
        Assert.Equal("C:\\work\\Tests.csproj", requested.Node.FullPath);

        vm.RequestAction(projectNode, CSharpSolutionAction.DebugTests);
        Assert.Equal(CSharpSolutionAction.DebugTests, requested.Action);

        vm.RequestAction(projectNode, CSharpSolutionAction.Run);
        Assert.Equal(CSharpSolutionAction.Run, requested.Action);
        vm.RequestAction(projectNode, CSharpSolutionAction.Debug);
        Assert.Equal(CSharpSolutionAction.Debug, requested.Action);

        vm.RequestAction(projectNode, CSharpSolutionAction.FixAllProject);
        Assert.Equal(CSharpSolutionAction.FixAllProject, requested.Action);

        var solutionNode = Assert.Single(vm.Nodes,
            node => node.Kind == CSharpSolutionNodeKind.Solution);
        vm.RequestAction(solutionNode, CSharpSolutionAction.FixAllSolution);
        Assert.Equal(CSharpSolutionAction.FixAllSolution, requested.Action);

        requested = null;
        vm.RequestAction(solutionNode, CSharpSolutionAction.FixAllProject);
        Assert.Null(requested);
    }

    [Fact]
    public async Task Solution_explorer_exposes_and_switches_the_solution_configuration()
    {
        var project = new ProjectModel("App", "C:\\work\\App.csproj", "C:\\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [], [], [], [])
        ], "net10.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(
            "C:\\work\\App.sln", "App", "C:\\work", [project], ProjectLoadState.Ready,
            Configurations: ["Debug", "Release"], SelectedConfiguration: "Debug"));
        using var vm = new CSharpSolutionExplorerViewModel(service);

        vm.SelectedConfiguration = "Release";
        await Task.Delay(50);

        Assert.True(vm.HasMultipleConfigurations);
        Assert.Equal("Release", service.Current.EffectiveConfiguration);
        Assert.Equal("Release", vm.SelectedConfiguration);
    }

    [Fact]
    public void TFMが1つならその段は挟まずファイルを直接ぶら下げる()
    {
        // 「プロジェクトを開く→net10.0を開く」の二度手間を無くす。選択肢が1つしか無い段は
        // 選ばせる意味が無いので、TFM名は行末の添え字（Detail）へ落とす。
        var project = new ProjectModel("App", @"C:\work\App.csproj", @"C:\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Program.cs", @"C:\work\Program.cs")], [], [], [])],
            "net10.0", true, ProjectLoadState.Ready);
        var tree = CSharpSolutionTreeBuilder.Build(
            new SolutionModel(@"C:\work\App.sln", "App", @"C:\work", [project], ProjectLoadState.Ready));

        var projectNode = Assert.Single(tree.Children);
        Assert.DoesNotContain(projectNode.Children,
            node => node.Kind == CSharpSolutionNodeKind.TargetFramework);
        Assert.Contains(projectNode.Children,
            node => node is { Name: "Program.cs", Kind: CSharpSolutionNodeKind.File });
        Assert.Equal("テスト", projectNode.Detail);
        Assert.Equal("1 プロジェクト", tree.Detail);
    }

    [Fact]
    public void 絞り込みは一致した枝だけを残し上限で打ち切る()
    {
        var project = new ProjectModel("App", @"C:\work\App.csproj", @"C:\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem(@"Views\UserView.cs", @"C:\work\Views\UserView.cs"),
                new ProjectItem(@"Views\OrderView.cs", @"C:\work\Views\OrderView.cs"),
                new ProjectItem("Program.cs", @"C:\work\Program.cs"),
            ], [], [], [])], "net10.0", false, ProjectLoadState.Ready);
        var tree = CSharpSolutionTreeBuilder.Build(
            new SolutionModel(@"C:\work\App.sln", "App", @"C:\work", [project], ProjectLoadState.Ready));

        var hit = CSharpSolutionTreeFilter.Apply(tree, "user");
        var views = Assert.Single(Assert.Single(hit.Root!.Children).Children);
        Assert.Equal("Views", views.Name);
        Assert.Equal("UserView.cs", Assert.Single(views.Children).Name);
        Assert.Equal(1, hit.Matched);
        Assert.False(hit.Truncated);

        // 空白区切りはAND。
        Assert.Equal(0, CSharpSolutionTreeFilter.Apply(tree, "user order").Matched);
        // 一致なしは枝ごと消える（ソリューション行だけが残らない）。
        Assert.Null(CSharpSolutionTreeFilter.Apply(tree, "存在しない").Root);
        // 上限を超えたぶんは落として Truncated を立てる（全開表示を守るため）。
        var capped = CSharpSolutionTreeFilter.Apply(tree, ".cs", limit: 2);
        Assert.Equal(2, capped.Matched);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public void ファイルを選んだままのビルドは持ち主のプロジェクトへ遡る()
    {
        // 以前はファイル選択中にビルドを押すと何も起きなかった（対象がプロジェクトでないため）。
        var project = new ProjectModel("App", @"C:\work\App.csproj", @"C:\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem(@"Views\UserView.cs", @"C:\work\Views\UserView.cs")], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
            new SolutionModel(@"C:\work\App.sln", "App", @"C:\work", [project], ProjectLoadState.Ready)));
        CSharpSolutionActionEventArgs? requested = null;
        vm.ActionRequested += (_, args) => requested = args;

        vm.SelectedNode = Find(vm.Nodes, n => n.Name == "UserView.cs");
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal("App", vm.ActionTargetLabel);
        Assert.False(vm.CanTestTarget);

        vm.RequestTargetAction(CSharpSolutionAction.Build);
        Assert.NotNull(requested);
        Assert.Equal(CSharpSolutionNodeKind.Project, requested!.Node.Kind);
        Assert.Equal(@"C:\work\App.csproj", requested.Node.FullPath);
    }

    [Fact]
    public void 絞り込みは結果を開いた状態で出し解除で元の開閉へ戻る()
    {
        var project = new ProjectModel("App", @"C:\work\App.csproj", @"C:\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem(@"Views\UserView.cs", @"C:\work\Views\UserView.cs"),
                new ProjectItem("Program.cs", @"C:\work\Program.cs"),
            ], [], [], [])], "net10.0", false, ProjectLoadState.Ready);
        using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
            new SolutionModel(@"C:\work\App.sln", "App", @"C:\work", [project], ProjectLoadState.Ready)));

        var views = Find(vm.Nodes, n => n.Name == "Views")!;
        Assert.False(views.IsExpanded);

        vm.FilterText = "UserView";
        Assert.True(vm.IsFiltering);
        Assert.Equal("1件", vm.FilterStatus);
        Assert.True(Find(vm.Nodes, n => n.Name == "Views")!.IsExpanded);
        Assert.Null(Find(vm.Nodes, n => n.Name == "Program.cs"));

        // 解除したら、絞り込みのために開いた枝は畳まれた元の状態へ戻る。
        vm.FilterText = "";
        Assert.False(Find(vm.Nodes, n => n.Name == "Views")!.IsExpanded);
        Assert.NotNull(Find(vm.Nodes, n => n.Name == "Program.cs"));
        Assert.Equal("", vm.FilterStatus);
    }

    [Fact]
    public void 絞り込み中の折りたたみはツリーを1度しか作り直さない()
    {
        var project = new ProjectModel("App", @"C:\work\App.csproj", @"C:\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem(@"Views\UserView.cs", @"C:\work\Views\UserView.cs"),
                new ProjectItem("Program.cs", @"C:\work\Program.cs"),
            ], [], [], [])], "net10.0", false, ProjectLoadState.Ready);
        using var vm = new CSharpSolutionExplorerViewModel(new FakeSolutionService(
            new SolutionModel(@"C:\work\App.sln", "App", @"C:\work", [project], ProjectLoadState.Ready)));

        vm.FilterText = "UserView";
        var rebuilds = 0;
        // ルートを差し込んだ回数＝作り直した回数。絞り込み解除とCollapseAllで2度作ると、
        // 1万ノード級のVMツリーを1回の折りたたみで2度捨てて作ることになる。
        vm.Nodes.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) rebuilds++;
        };

        vm.CollapseAllCommand.Execute(null);

        Assert.Equal(1, rebuilds);
        Assert.Equal("", vm.FilterText);
        Assert.False(vm.IsFiltering);
        Assert.False(Find(vm.Nodes, n => n.Name == "Views")!.IsExpanded);
        Assert.NotNull(Find(vm.Nodes, n => n.Name == "Program.cs"));
    }

    private static CSharpSolutionNodeViewModel? Find(
        IEnumerable<CSharpSolutionNodeViewModel> nodes,
        Func<CSharpSolutionNodeViewModel, bool> predicate)
    {
        foreach (var node in nodes)
        {
            if (predicate(node)) return node;
            var child = Find(node.Children, predicate);
            if (child is not null) return child;
        }
        return null;
    }

    private sealed class FakeSolutionService(SolutionModel initial) : ISolutionModelService
    {
        public SolutionModel Current { get; private set; } = initial;
        public event EventHandler<SolutionModel>? Changed;
        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);

        public Task<bool> SelectConfigurationAsync(string configuration,
            CancellationToken cancellationToken = default)
        {
            if (!Current.ConfigurationOptions.Contains(configuration, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(false);
            Current = Current with { SelectedConfiguration = configuration };
            Changed?.Invoke(this, Current);
            return Task.FromResult(true);
        }
    }
}
