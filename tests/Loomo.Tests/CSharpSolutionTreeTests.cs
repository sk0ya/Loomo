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
        Assert.Contains(selected.Children, n => n.Name == "None" && n.Kind == CSharpSolutionNodeKind.NoneFile);
    }

    [Fact]
    public void Preserves_link_path_for_files_outside_project_directory()
    {
        var project = new ProjectModel("App", "C:\\work\\App.csproj", "C:\\work", [], [
            new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem("..\\Shared\\Common.cs", "C:\\Shared\\Common.cs", "Shared\\Common.cs")
            ], [], [], [])], "net10.0", false, ProjectLoadState.Ready);
        var tree = CSharpSolutionTreeBuilder.Build(new SolutionModel(null, "App", "C:\\work", [project], ProjectLoadState.Ready));

        var tfm = Assert.Single(Assert.Single(tree.Children).Children);
        var shared = Assert.Single(tfm.Children, n => n.Name == "Shared");
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
