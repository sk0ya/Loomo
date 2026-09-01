using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>§33.6 のプロジェクト文脈（発見、MSBuild評価、TFM、逆引き、状態）の契約テスト。</summary>
public sealed class SolutionModelServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-csharp-" + Guid.NewGuid().ToString("N"));

    public SolutionModelServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Loads_solution_projects_and_each_target_framework_from_evaluator()
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(_root, "src", "App")).FullName;
        var projectPath = Path.Combine(projectDir, "App.csproj");
        var sourcePath = Path.Combine(projectDir, "Program.cs");
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(sourcePath, "class Program {}");
        File.WriteAllText(Path.Combine(_root, "App.sln"),
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"src\\App\\App.csproj\", \"{1}\"\nEndProject");

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation(null, "net8.0;net9.0", "TRACE;NET", "preview",
            [new("Program.cs", sourcePath)], [new("../Lib/Lib.csproj")],
            [new("stylecop.dll", Path.Combine(_root, "stylecop.dll"))], [], [], true,
            [new("StyleCop.Analyzers") ], Nullable: "enable"));
        evaluator.Set(projectPath, "net8.0", new ProjectEvaluation("net8.0", "", "TRACE;NET8_0", "latest",
            [new("Program.cs", sourcePath)], [], [], [], [], true, Nullable: "enable"));
        evaluator.Set(projectPath, "net9.0", new ProjectEvaluation("net9.0", "", "TRACE;NET9_0", "latest",
            [new("Program.cs", sourcePath)], [], [], [], [], true, Nullable: "enable"));

        using var service = new SolutionModelService(workspace, evaluator);
        var changed = new List<ProjectLoadState>();
        service.Changed += (_, model) => changed.Add(model.State);
        var model = await service.ReloadAsync();

        var project = Assert.Single(model.Projects);
        Assert.Equal(ProjectLoadState.Ready, model.State);
        Assert.Equal(["net8.0", "net9.0"], project.TargetFrameworks.Select(t => t.Name));
        Assert.Equal("net8.0", project.SelectedTargetFramework);
        Assert.True(project.IsTestProject);
        Assert.True(project.TargetFrameworks.All(target => target.NullableEnabled));
        Assert.Equal(["StyleCop.Analyzers"], project.PackageReferences);
        Assert.Equal(sourcePath, project.SelectedTargetFrameworkModel!.CompileFiles.Single().FullPath);
        Assert.Empty(project.SelectedTargetFrameworkModel.ProjectReferences!);
        Assert.Same(project, service.ProjectForFile(sourcePath));
        Assert.Same(project, model.ProjectForTarget(projectPath));
        Assert.Equal([null, "net8.0", "net9.0"], evaluator.Requests.Select(x => x.TargetFramework));
        Assert.Contains(ProjectLoadState.Loading, changed);
    }

    [Fact]
    public void File_only_in_an_unselected_target_framework_is_not_in_the_active_project_context()
    {
        var selectedPath = Path.Combine(_root, "Selected.cs");
        var otherTargetPath = Path.Combine(_root, "OnlyNet9.cs");
        var projectPath = Path.Combine(_root, "Multi.csproj");
        var project = new ProjectModel("Multi", projectPath, _root, [],
            [new TargetFrameworkModel("net8.0", [], "latest",
                [new ProjectItem("Selected.cs", selectedPath)], [], [], []),
             new TargetFrameworkModel("net9.0", [], "latest",
                [new ProjectItem("OnlyNet9.cs", otherTargetPath)], [], [], [])],
            "net8.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(null, "Multi", _root, [project], ProjectLoadState.Ready);

        Assert.Same(project, solution.ProjectForFile(selectedPath));
        Assert.Null(solution.ProjectForFile(otherTargetPath));
        Assert.Equal(ProjectLoadState.NotInSelectedTargetFramework, solution.ResolveFileState(otherTargetPath));
    }

    [Fact]
    public async Task Loads_many_projects_in_solution_order_with_bounded_parallelism()
    {
        var projectPaths = new[] { "A.csproj", "B.csproj", "C.csproj" }
            .Select(name => Path.Combine(_root, name)).ToArray();
        foreach (var projectPath in projectPaths) File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(Path.Combine(_root, "Many.sln"), """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "A.csproj", "{1}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "B.csproj", "{2}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "C", "C.csproj", "{3}"
            EndProject
            """);

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator { DelayMilliseconds = 20 };
        foreach (var projectPath in projectPaths)
            evaluator.Set(projectPath, null,
                new ProjectEvaluation("net10.0", "", "", "latest", [], [], [], [], [], false));

        using var service = new SolutionModelService(workspace, evaluator);
        var model = await service.ReloadAsync();

        Assert.Equal(["A", "B", "C"], model.Projects.Select(project => project.Name));
        Assert.InRange(evaluator.MaxConcurrency, 2, 4);
    }

    [Fact]
    public async Task Real_msbuild_loads_a_large_project_graph_without_losing_order_or_references()
    {
        const int projectCount = 24;
        var projects = new List<string>(projectCount);
        var solutionLines = new List<string>
        {
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
        };

        for (var index = 0; index < projectCount; index++)
        {
            var name = $"Layer{index:D2}";
            var projectPath = Path.Combine(_root, name + ".csproj");
            var reference = index == 0
                ? ""
                : $"    <ProjectReference Include=\"{projects[index - 1]}\" />\n";
            File.WriteAllText(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                {reference}  </ItemGroup>
                </Project>
                """);
            var sourcePath = Path.Combine(_root, name + ".cs");
            File.WriteAllText(sourcePath, $"public sealed class {name} {{ }}\n");
            projects.Add(projectPath);

            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            solutionLines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{name}.csproj\", \"{projectGuid}\"");
            solutionLines.Add("EndProject");
        }
        solutionLines.AddRange(["Global", "EndGlobal"]);
        File.WriteAllLines(Path.Combine(_root, "Large.sln"), solutionLines);

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        using var service = new SolutionModelService(workspace, new MsBuildProjectEvaluator());

        var model = await service.ReloadAsync();

        Assert.Equal(ProjectLoadState.Ready, model.State);
        Assert.Equal(projectCount, model.Projects.Count);
        Assert.Equal(Enumerable.Range(0, projectCount).Select(index => $"Layer{index:D2}"),
            model.Projects.Select(project => project.Name));
        var last = Assert.Single(model.Projects, project => project.Name == "Layer23");
        Assert.Contains(projects[^2], last.ProjectReferences, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("net10.0", last.SelectedTargetFramework);
        Assert.All(model.Projects, project =>
            Assert.Equal(ProjectLoadState.Ready, project.State));
    }

    [Fact]
    public async Task Selected_target_framework_is_published_and_survives_reload()
    {
        var projectPath = Path.Combine(_root, "App.csproj");
        File.WriteAllText(projectPath, "<Project />");
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation(null, "net8.0;net9.0", "", "latest",
            [], [], [], [], [], false));
        evaluator.Set(projectPath, "net8.0", new ProjectEvaluation("net8.0", "", "", "latest",
            [], [], [], [], [], false));
        evaluator.Set(projectPath, "net9.0", new ProjectEvaluation("net9.0", "", "", "latest",
            [], [], [], [], [], false));

        using var service = new SolutionModelService(workspace, evaluator);
        await service.ReloadAsync();

        Assert.True(await service.SelectTargetFrameworkAsync(projectPath, "net9.0"));
        Assert.Equal("net9.0", service.Current.Projects.Single().SelectedTargetFramework);
        await service.ReloadAsync();

        Assert.Equal("net9.0", service.Current.Projects.Single().SelectedTargetFramework);
    }

    [Fact]
    public async Task Solution_configuration_is_read_and_reused_for_re_evaluation()
    {
        var projectPath = Path.Combine(_root, "App.csproj");
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(Path.Combine(_root, "App.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App.csproj", "{1}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Debug|Any CPU = Debug|Any CPU
                    Release|Any CPU = Release|Any CPU
                EndGlobalSection
            EndGlobal
            """);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation("net10.0", "", "DEBUG", "latest",
            [], [], [], [], [], false));
        using var service = new SolutionModelService(workspace, evaluator);

        var initial = await service.ReloadAsync();

        Assert.Equal(["Debug", "Release"], initial.ConfigurationOptions);
        Assert.Equal("Debug", initial.EffectiveConfiguration);
        Assert.Contains(evaluator.ConfigurationRequests, r => r.Configuration == "Debug");

        Assert.True(await service.SelectConfigurationAsync("Release"));

        Assert.Equal("Release", service.Current.SelectedConfiguration);
        Assert.Equal("Release", service.Current.EffectiveConfiguration);
        Assert.Contains(evaluator.ConfigurationRequests, r => r.Configuration == "Release");
        Assert.False(await service.SelectConfigurationAsync("ProfileThatDoesNotExist"));
    }

    [Fact]
    public async Task Solution_configuration_mapping_is_applied_per_project()
    {
        var projectPath = Path.Combine(_root, "App.csproj");
        const string projectGuid = "11111111-1111-1111-1111-111111111111";
        File.WriteAllText(projectPath, "<Project />");
        var solutionPath = Path.Combine(_root, "App.sln");
        File.WriteAllLines(solutionPath,
        [
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App.csproj\", \"{" + projectGuid + "}\"",
            "EndProject",
            "Global",
            "    GlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "        Debug|Any CPU = Debug|Any CPU",
            "        Release|Any CPU = Release|Any CPU",
            "    EndGlobalSection",
            "    GlobalSection(ProjectConfigurationPlatforms) = postSolution",
            "        {" + projectGuid + "}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            "        {" + projectGuid + "}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            "        {" + projectGuid + "}.Release|Any CPU.ActiveCfg = Profile|Any CPU",
            "        {" + projectGuid + "}.Release|Any CPU.Build.0 = Profile|Any CPU",
            "    EndGlobalSection",
            "EndGlobal",
        ]);

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation("net10.0", "", "RELEASE_MAPPING", "latest",
            [], [], [], [], [], false));
        using var service = new SolutionModelService(workspace, evaluator);

        await service.ReloadAsync();
        Assert.Equal("Debug", service.Current.Projects.Single().Configuration);
        Assert.True(await service.SelectConfigurationAsync("Release"));

        var project = Assert.Single(service.Current.Projects);
        Assert.Equal("Profile", project.Configuration);
        Assert.Contains(evaluator.ConfigurationRequests, request => request.Configuration == "Profile");
        Assert.Equal("Profile", SolutionProjectDiscovery.ResolveProjectConfiguration(
            solutionPath, projectPath, "Release"));
    }

    [Fact]
    public void Configuration_for_target_uses_project_mapping_and_solution_fallback()
    {
        var projectPath = Path.Combine(_root, "Mapped.csproj");
        File.WriteAllText(projectPath, "<Project />");
        var project = new ProjectModel("Mapped", projectPath, _root, [], [], null,
            false, ProjectLoadState.Ready) { Configuration = "Profile" };
        var solution = new SolutionModel(
            Path.Combine(_root, "Mapped.sln"), "Mapped", _root, [project],
            ProjectLoadState.Ready, Configurations: ["Debug", "Release"],
            SelectedConfiguration: "Release");

        Assert.Equal("Profile", solution.ConfigurationForTarget(projectPath));
        Assert.Equal("Release", solution.ConfigurationForTarget(
            Path.Combine(_root, "Mapped.sln")));
        Assert.Equal("Release", solution.ConfigurationForTarget(null));
    }

    [Fact]
    public async Task Supports_slnx_and_reports_file_outside_project_as_not_in_project()
    {
        var projectPath = Path.Combine(_root, "App.csproj");
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution><Project Path=\"App.csproj\" /></Solution>");
        var sourcePath = Path.Combine(_root, "App.cs");
        var externalPath = Path.Combine(_root, "notes.cs");
        File.WriteAllText(sourcePath, "class App {}");
        File.WriteAllText(externalPath, "class Notes {}");

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation("net10.0", "", "", "default",
            [new("App.cs", sourcePath)], [], [], [], [], false));

        using var service = new SolutionModelService(workspace, evaluator);
        var model = await service.ReloadAsync();

        Assert.Equal("App", model.Name);
        Assert.NotNull(service.ProjectForFile(sourcePath));
        Assert.Null(service.ProjectForFile(externalPath));
        Assert.Equal(ProjectLoadState.NotInProject, service.FileState(externalPath));
    }

    [Fact]
    public async Task Malformed_solution_falls_back_to_projects_in_the_workspace()
    {
        var projectPath = Path.Combine(_root, "Fallback.csproj");
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(Path.Combine(_root, "Broken.slnx"), "<Solution><Project");

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation("net10.0", "", "", "latest",
            [], [], [], [], [], false));

        using var service = new SolutionModelService(workspace, evaluator);
        var model = await service.ReloadAsync();

        Assert.Equal(ProjectLoadState.Ready, model.State);
        Assert.Equal("Fallback", Assert.Single(model.Projects).Name);
    }

    [Fact]
    public async Task Missing_workspace_folder_does_not_throw_during_reload()
    {
        var missing = Path.Combine(_root, "removed");
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(missing);

        using var service = new SolutionModelService(workspace, new FakeEvaluator());
        var model = await service.ReloadAsync();

        Assert.Equal(ProjectLoadState.Ready, model.State);
        Assert.Empty(model.Projects);
    }

    [Fact]
    public async Task Evaluation_failure_is_visible_and_does_not_publish_ready_model()
    {
        var projectPath = Path.Combine(_root, "Broken.csproj");
        File.WriteAllText(projectPath, "<Project />");
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator { Exception = new InvalidOperationException("MSBuild error") };
        using var service = new SolutionModelService(workspace, evaluator);

        var model = await service.ReloadAsync();

        Assert.Equal(ProjectLoadState.Failed, model.State);
        Assert.Contains("MSBuild error", model.Error);
        Assert.Equal(ProjectLoadState.Failed, Assert.Single(model.Projects).State);
        Assert.Equal(ProjectLoadState.Failed, service.FileState(Path.Combine(_root, "anything.cs")));
    }

    [Fact]
    public async Task Real_msbuild_evaluator_returns_resolved_compile_items_and_properties()
    {
        var projectPath = Path.Combine(_root, "Real.csproj");
        var sourcePath = Path.Combine(_root, "Program.cs");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>preview</LangVersion><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(sourcePath, "class Program {}");

        var result = await new MsBuildProjectEvaluator().EvaluateAsync(projectPath, null);

        Assert.Equal("net10.0", result.TargetFramework);
        Assert.Equal("preview", result.LangVersion);
        Assert.Equal("enable", result.Nullable);
        Assert.Contains(result.Compile, item => item.FullPath is not null &&
            string.Equals(Path.GetFullPath(item.FullPath), sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Real_msbuild_evaluator_honors_configuration()
    {
        var projectPath = Path.Combine(_root, "Configured.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'"><DefineConstants>RELEASE_MARKER</DefineConstants></PropertyGroup>
            </Project>
            """);

        var result = await new MsBuildProjectEvaluator().EvaluateAsync(projectPath, null, "Release");

        Assert.Contains("RELEASE_MARKER", result.DefineConstants);
    }

    [Theory]
    [InlineData(".editorconfig")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("stylecop.json")]
    [InlineData("Rules.ruleset")]
    [InlineData("App.csproj")]
    [InlineData("App.sln")]
    [InlineData("App.slnx")]
    public void Configuration_files_trigger_project_re_evaluation(string name)
        => Assert.True(SolutionModelService.IsConfigurationFile(Path.Combine(_root, name)));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("notes.md")]
    [InlineData(@"bin\Debug\App.dll")]
    public void Unrelated_files_do_not_trigger_project_re_evaluation(string name)
        => Assert.False(SolutionModelService.IsConfigurationFile(Path.Combine(_root, name)));

    [Fact]
    public async Task Configuration_file_change_debounces_and_reloads_the_solution()
    {
        var projectPath = Path.Combine(_root, "App.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var evaluator = new FakeEvaluator();
        evaluator.Set(projectPath, null, new ProjectEvaluation("net10.0", "", "", "default",
            [], [], [], [], [], false));
        using var service = new SolutionModelService(workspace, evaluator);
        await service.ReloadAsync();

        var reloaded = new TaskCompletionSource<SolutionModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, model) =>
        {
            if (model.State == ProjectLoadState.Ready && evaluator.Requests.Count > 1)
                reloaded.TrySetResult(model);
        };

        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "root = true\n");
        var completed = await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(reloaded.Task, completed);
        Assert.True(evaluator.Requests.Count > 1);
    }

    private sealed class FakeEvaluator : IProjectEvaluator
    {
        private readonly Dictionary<(string Path, string? TargetFramework), ProjectEvaluation> _values = new();
        private readonly object _gate = new();
        private int _activeEvaluations;
        private int _maxConcurrency;
        public List<(string Path, string? TargetFramework)> Requests { get; } = [];
        public List<(string Path, string? TargetFramework, string? Configuration)> ConfigurationRequests { get; } = [];
        public Exception? Exception { get; init; }
        public int DelayMilliseconds { get; init; }
        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public void Set(string path, string? targetFramework, ProjectEvaluation value)
            => _values[(Path.GetFullPath(path), targetFramework)] = value;

        public Task<ProjectEvaluation> EvaluateAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken = default)
        {
            lock (_gate) Requests.Add((Path.GetFullPath(projectPath), targetFramework));
            if (Exception is not null) throw Exception;
            return EvaluateCoreAsync(projectPath, targetFramework, cancellationToken);
        }

        public Task<ProjectEvaluation> EvaluateAsync(string projectPath, string? targetFramework,
            string? configuration, CancellationToken cancellationToken = default)
        {
            lock (_gate) ConfigurationRequests.Add((Path.GetFullPath(projectPath), targetFramework, configuration));
            return EvaluateAsync(projectPath, targetFramework, cancellationToken);
        }

        private async Task<ProjectEvaluation> EvaluateCoreAsync(
            string projectPath, string? targetFramework, CancellationToken cancellationToken)
        {
            if (DelayMilliseconds > 0)
            {
                var active = Interlocked.Increment(ref _activeEvaluations);
                UpdateMaxConcurrency(active);
                try { await Task.Delay(DelayMilliseconds, cancellationToken); }
                finally { Interlocked.Decrement(ref _activeEvaluations); }
            }

            return _values[(Path.GetFullPath(projectPath), targetFramework)];
        }

        private void UpdateMaxConcurrency(int value)
        {
            var observed = Volatile.Read(ref _maxConcurrency);
            while (value > observed)
            {
                var original = Interlocked.CompareExchange(ref _maxConcurrency, value, observed);
                if (original == observed) return;
                observed = original;
            }
        }
    }
}
