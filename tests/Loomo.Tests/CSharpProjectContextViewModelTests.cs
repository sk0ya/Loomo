using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpProjectContextViewModelTests
{
    [Fact]
    public void Non_CSharp_file_is_hidden()
    {
        var service = new FakeSolutionService(SolutionModel.NotConfigured("C:\\work"));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile("C:\\work\\README.md");

        Assert.False(vm.IsVisible);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void Loading_state_is_not_shown_as_no_project()
    {
        var solution = new SolutionModel(null, "work", "C:\\work", [], ProjectLoadState.Loading);
        var service = new FakeSolutionService(solution);
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile("C:\\work\\App.cs");

        Assert.True(vm.IsVisible);
        Assert.Equal(ProjectLoadState.Loading, vm.State);
        Assert.Contains("解析中", vm.StatusText);
    }

    [Fact]
    public void Ready_project_shows_project_and_target_framework()
    {
        const string root = "C:\\work";
        const string file = "C:\\work\\App.cs";
        var project = new ProjectModel("App", "C:\\work\\App.csproj", root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("App.cs", file)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel("C:\\work\\work.sln", "work", root,
            [project], ProjectLoadState.Ready));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile(file);

        Assert.True(vm.IsVisible);
        Assert.Same(project, vm.Project);
        Assert.Equal("App · net10.0", vm.StatusText);
        Assert.Equal("参照 0 · Analyzer 0", vm.ProjectStructureSummary);
    }

    [Fact]
    public void Status_tooltip_exposes_solution_to_project_configuration_mapping()
    {
        const string root = "C:\\work";
        const string file = "C:\\work\\App.cs";
        var project = new ProjectModel("App", "C:\\work\\App.csproj", root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("App.cs", file)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready)
        { Configuration = "Profile" };
        var service = new FakeSolutionService(new SolutionModel("C:\\work\\work.sln", "work", root,
            [project], ProjectLoadState.Ready, Configurations: ["Debug", "Release"],
            SelectedConfiguration: "Release"));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile(file);

        Assert.Contains("Configuration: Release → Profile", vm.StatusToolTip);
    }

    [Fact]
    public void Ready_state_exposes_the_effective_editorconfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-context-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "App.cs");
            File.WriteAllText(Path.Combine(root, ".editorconfig"), "root = true\n[*.cs]\nindent_size = 2\n");
            File.WriteAllText(source, "class App {}\n");
            var project = new ProjectModel("App", Path.Combine(root, "App.csproj"), root, [],
                [new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem("App.cs", source)], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready);
            var solution = new FakeSolutionService(new SolutionModel(null, "work", root,
                [project], ProjectLoadState.Ready));
            using var vm = new CSharpProjectContextViewModel(solution, new CSharpEditorConfigService());

            vm.SetCurrentFile(source);

            Assert.Equal(".editorconfig ×1", vm.EditorConfigSummary);
            Assert.Equal(2, vm.EditorConfig!.IndentSize);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Multi_target_project_exposes_and_switches_the_selected_framework()
    {
        const string root = "C:\\work";
        const string file = "C:\\work\\App.cs";
        var project = new ProjectModel("App", "C:\\work\\App.csproj", root, [],
            [new TargetFrameworkModel("net8.0", [], "latest", [new ProjectItem("App.cs", file)], [], [], []),
             new TargetFrameworkModel("net9.0", [], "latest", [new ProjectItem("App.cs", file)], [], [], [])],
            "net8.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(null, "work", root, [project], ProjectLoadState.Ready));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile(file);
        vm.SelectedTargetFramework = "net9.0";

        Assert.True(vm.HasMultipleTargetFrameworks);
        Assert.Equal(["net8.0", "net9.0"], vm.TargetFrameworkOptions);
        Assert.Equal("net9.0", vm.Project!.SelectedTargetFramework);
    }

    [Fact]
    public async Task Configuration_selector_switches_the_shared_solution_configuration()
    {
        const string root = "C:\\work";
        const string file = "C:\\work\\App.cs";
        var project = new ProjectModel("App", "C:\\work\\App.csproj", root, [],
            [new TargetFrameworkModel("net10.0", [], "latest", [new ProjectItem("App.cs", file)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(null, "work", root, [project],
            ProjectLoadState.Ready, Configurations: ["Debug", "Release"], SelectedConfiguration: "Debug"));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile(file);
        vm.SelectedConfiguration = "Release";
        await Task.Delay(50);

        Assert.Equal(["Debug", "Release"], vm.ConfigurationOptions);
        Assert.Equal("Release", service.Current.EffectiveConfiguration);
        Assert.Equal("Release", vm.SelectedConfiguration);
    }

    [Fact]
    public void File_outside_project_is_explicitly_labeled()
    {
        var project = new ProjectModel("App", "C:\\work\\App.csproj", "C:\\work", [],
            [new TargetFrameworkModel("net10.0", [], "latest", [], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(null, "work", "C:\\work",
            [project], ProjectLoadState.Ready));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile("C:\\work\\Loose.cs");

        Assert.True(vm.IsVisible);
        Assert.Equal(ProjectLoadState.NotInProject, vm.State);
        Assert.Contains("プロジェクト外", vm.StatusText);
    }

    [Fact]
    public async Task File_in_another_target_framework_can_switch_into_the_active_project_context()
    {
        const string root = "C:\\work";
        const string file = "C:\\work\\OnlyNet9.cs";
        var project = new ProjectModel("App", "C:\\work\\App.csproj", root, [],
            [new TargetFrameworkModel("net8.0", [], "latest", [], [], [], []),
             new TargetFrameworkModel("net9.0", [], "latest",
                 [new ProjectItem("OnlyNet9.cs", file)], [], [], [])],
            "net8.0", false, ProjectLoadState.Ready);
        var service = new FakeSolutionService(new SolutionModel(null, "work", root,
            [project], ProjectLoadState.Ready));
        using var vm = new CSharpProjectContextViewModel(service);

        vm.SetCurrentFile(file);

        Assert.True(vm.IsVisible);
        Assert.Equal(ProjectLoadState.NotInSelectedTargetFramework, vm.State);
        Assert.NotNull(vm.Project);
        Assert.Equal(["net8.0", "net9.0"], vm.TargetFrameworkOptions);
        Assert.Contains("別TFM", vm.StatusText);
        Assert.Contains("TargetFramework", vm.StatusToolTip);

        vm.SelectedTargetFramework = "net9.0";
        await Task.Delay(50);

        Assert.Equal(ProjectLoadState.Ready, vm.State);
        Assert.NotNull(vm.Project);
        Assert.Equal("net9.0", vm.Project.SelectedTargetFramework);
        Assert.Contains("App", vm.StatusText);
    }

    private sealed class FakeSolutionService(SolutionModel initial) : ISolutionModelService
    {
        public SolutionModel Current { get; private set; } = initial;
        public event EventHandler<SolutionModel>? Changed;

        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);

        public Task<bool> SelectTargetFrameworkAsync(string projectPath, string targetFramework,
            CancellationToken cancellationToken = default)
        {
            var project = Current.Projects.FirstOrDefault(p =>
                string.Equals(p.FullPath, projectPath, StringComparison.OrdinalIgnoreCase));
            if (project is null || project.TargetFrameworks.All(t =>
                    !string.Equals(t.Name, targetFramework, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(false);
            var updated = project with { SelectedTargetFramework = targetFramework };
            Current = Current with { Projects = [updated] };
            Changed?.Invoke(this, Current);
            return Task.FromResult(true);
        }

        public Task<bool> SelectConfigurationAsync(string configuration,
            CancellationToken cancellationToken = default)
        {
            if (!Current.ConfigurationOptions.Any(c =>
                    string.Equals(c, configuration, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(false);
            Current = Current with { SelectedConfiguration = configuration };
            Changed?.Invoke(this, Current);
            return Task.FromResult(true);
        }

        public void Publish(SolutionModel model)
        {
            Current = model;
            Changed?.Invoke(this, model);
        }
    }
}
