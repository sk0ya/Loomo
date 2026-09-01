using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

[Collection(CSharpExternalProcessCollection.Name)]
public sealed class StyleCopDiagnosticServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LoomoStyleCopTests", Guid.NewGuid().ToString("N"));

    public StyleCopDiagnosticServiceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "root = true\n[*.cs]\ndotnet_diagnostic.SA1101.severity = error\n");
        File.WriteAllText(Path.Combine(_root, "stylecop.json"), "{}");
    }

    [Fact]
    public async Task Official_analyzer_reports_unsaved_StyleCop_diagnostic_with_configured_severity()
    {
        var sourcePath = Path.Combine(_root, "Sample.cs");
        var source = "public sealed class Sample { private int _value; public int Get() => _value; }";
        File.WriteAllText(sourcePath, source);
        var analyzerPath = FindStyleCopAnalyzer();
        var project = CreateProject(sourcePath, analyzerPath);

        var result = await new StyleCopDiagnosticService().AnalyzeAsync(project, sourcePath, source);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "SA1101");
        Assert.Equal(Editor.Core.Lsp.DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("StyleCop", diagnostic.Source);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Editorconfig_none_suppresses_the_official_analyzer_diagnostic()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "root = true\n[*.cs]\ndotnet_diagnostic.SA1101.severity = none\n");
        var sourcePath = Path.Combine(_root, "Sample.cs");
        var source = "public sealed class Sample { private int _value; public int Get() => _value; }";
        File.WriteAllText(sourcePath, source);

        var result = await new StyleCopDiagnosticService().AnalyzeAsync(
            CreateProject(sourcePath, FindStyleCopAnalyzer()), sourcePath, source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "SA1101");
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Does_not_report_an_analyzer_not_loaded_state_as_a_style_violation()
    {
        var sourcePath = Path.Combine(_root, "MissingAnalyzer.cs");
        var source = "public sealed class Sample { private int _value; }";
        File.WriteAllText(sourcePath, source);
        var project = CreateProject(sourcePath,
            Path.Combine(_root, "missing", "StyleCop.Analyzers.dll"));

        var result = await new StyleCopDiagnosticService().AnalyzeAsync(project, sourcePath, source);

        Assert.Empty(result.Diagnostics);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Fixture_IDE_diagnostic_matches_build_diagnostic_for_unsaved_source()
    {
        var fixtureRoot = FindFixtureRoot();
        var projectPath = Path.Combine(fixtureRoot, "src", "Feature", "Feature.csproj");
        var sourcePath = Path.Combine(fixtureRoot, "src", "Feature", "FeatureService.cs");
        var source = await File.ReadAllTextAsync(sourcePath);
        var evaluation = await new MsBuildProjectEvaluator().EvaluateAsync(projectPath, "net10.0", "Debug");
        Assert.Contains(evaluation.Analyzers,
            item => item.FullPath?.EndsWith("StyleCop.Analyzers.dll", StringComparison.OrdinalIgnoreCase) == true);
        var project = CreateProjectModel(projectPath, evaluation, "net10.0");

        var ideResult = await new StyleCopDiagnosticService().AnalyzeAsync(project, sourcePath, source);
        var ideDiagnostic = Assert.Single(ideResult.Diagnostics, d => d.Code == "SA1101");
        Assert.Equal(Editor.Core.Lsp.DiagnosticSeverity.Error, ideDiagnostic.Severity);
        Assert.Equal(9, ideDiagnostic.Range.Start.Line);

        var build = await RunDotnetAsync(fixtureRoot,
            "build", projectPath, "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
            "-p:TargetFramework=net10.0");
        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("error SA1101", build.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Official_codefix_provider_returns_a_workspace_edit_for_SA1101()
    {
        var sourcePath = Path.Combine(_root, "Sample.cs");
        var source = "public sealed class Sample { private int _value; public int Get() => _value; }";
        File.WriteAllText(sourcePath, source);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.SA1101.severity = error\n");
        var project = CreateProject(sourcePath, FindStyleCopAnalyzer());
        var diagnostics = await new StyleCopDiagnosticService().AnalyzeAsync(project, sourcePath, source);
        var diagnostic = Assert.Single(diagnostics.Diagnostics, d => d.Code == "SA1101");

        var result = await new StyleCopCodeFixService().ApplyAsync(project, sourcePath, source, diagnostic);

        Assert.Null(result.Error);
        Assert.NotNull(result.Edit);
        var replacement = Assert.Single(result.Edit!.Changes.Values).Single();
        Assert.Contains("this._value", replacement.NewText);
    }

    [Fact]
    public async Task Official_codefix_provider_can_fix_all_diagnostics_in_a_project()
    {
        var sourcePath = Path.Combine(_root, "Sample.cs");
        var source = "public sealed class Sample { private int _value; public int Get() => _value; }";
        File.WriteAllText(sourcePath, source);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.SA1101.severity = error\n");
        var project = CreateProject(sourcePath, FindStyleCopAnalyzer());

        var result = await new StyleCopCodeFixService().ApplyAllAsync(project, [sourcePath]);

        Assert.Null(result.Error);
        Assert.Equal(1, result.DocumentsScanned);
        Assert.True(result.ActionsFound > 0);
        var replacement = Assert.Single(result.Edit!.Changes.Values).Single();
        Assert.Contains("this.value", replacement.NewText);
    }

    [Fact]
    public async Task Official_codefix_provider_reanalyzes_after_each_fix_in_a_multi_diagnostic_file()
    {
        var sourcePath = Path.Combine(_root, "Multiple.cs");
        var source = "public sealed class Sample { private int one; private int two; public int Get() => one + two; }";
        File.WriteAllText(sourcePath, source);
        File.WriteAllText(Path.Combine(_root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.SA1101.severity = error\n");
        var project = CreateProject(sourcePath, FindStyleCopAnalyzer());

        var result = await new StyleCopCodeFixService().ApplyAllAsync(project, [sourcePath]);

        Assert.Null(result.Error);
        Assert.True(result.ActionsFound >= 2);
        var replacement = Assert.Single(result.Edit!.Changes.Values).Single();
        Assert.Contains("this.one", replacement.NewText);
        Assert.Contains("this.two", replacement.NewText);
    }

    [Fact]
    public async Task Analyzer_uses_the_selected_target_framework_preprocessor_symbols()
    {
        var sourcePath = Path.Combine(_root, "Conditional.cs");
        var source = "#if FEATURE\npublic sealed class Sample { private int value; public int Get() => value; }\n#endif\n";
        File.WriteAllText(sourcePath, source);
        var analyzerPath = FindStyleCopAnalyzer();
        var item = new ProjectItem("Conditional.cs", sourcePath);
        var net9 = new TargetFrameworkModel("net9.0", [], "default", [item],
            [new ProjectItem("StyleCop.Analyzers.dll", analyzerPath)], [], [])
        {
            Nullable = "disable",
        };
        var net10 = new TargetFrameworkModel("net10.0", ["FEATURE"], "preview", [item],
            [new ProjectItem("StyleCop.Analyzers.dll", analyzerPath)], [], [])
        {
            Nullable = "enable",
        };
        var project = new ProjectModel(Path.GetFileNameWithoutExtension(Path.Combine(_root, "Conditional.csproj")),
            Path.Combine(_root, "Conditional.csproj"), _root, [], [net9, net10], "net9.0", false,
            ProjectLoadState.Ready)
        {
            PackageReferences = ["StyleCop.Analyzers"],
        };

        var disabled = await new StyleCopDiagnosticService().AnalyzeAsync(project, sourcePath, source);
        Assert.DoesNotContain(disabled.Diagnostics, diagnostic => diagnostic.Code == "SA1101");

        var enabledProject = project with { SelectedTargetFramework = "net10.0" };
        var enabled = await new StyleCopDiagnosticService().AnalyzeAsync(enabledProject, sourcePath, source);
        Assert.Contains(enabled.Diagnostics, diagnostic => diagnostic.Code == "SA1101");
    }

    [Fact]
    public async Task Official_codefix_provider_can_fix_all_diagnostics_across_project_files()
    {
        var firstPath = Path.Combine(_root, "First.cs");
        var secondPath = Path.Combine(_root, "Second.cs");
        File.WriteAllText(firstPath, "public sealed class First { private int value; public int Get() => value; }");
        File.WriteAllText(secondPath, "public sealed class Second { private int count; public int Get() => count; }");
        var analyzerPath = FindStyleCopAnalyzer();
        var target = new TargetFrameworkModel("net10.0", [], "latest",
            [new ProjectItem("First.cs", firstPath), new ProjectItem("Second.cs", secondPath)],
            [new ProjectItem("StyleCop.Analyzers.dll", analyzerPath)], [], []);
        var project = new ProjectModel("Multi", Path.Combine(_root, "Multi.csproj"), _root, [], [target],
            "net10.0", false, ProjectLoadState.Ready)
        {
            PackageReferences = ["StyleCop.Analyzers"],
        };

        var result = await new StyleCopCodeFixService().ApplyAllAsync(project, [firstPath, secondPath]);

        Assert.Null(result.Error);
        Assert.Equal(2, result.DocumentsScanned);
        Assert.True(result.ActionsFound >= 2);
        Assert.Equal(2, result.Edit!.Changes.Count);
        Assert.Contains("this.value", result.Edit.Changes[LspUri.FromPath(firstPath)].Single().NewText);
        Assert.Contains("this.count", result.Edit.Changes[LspUri.FromPath(secondPath)].Single().NewText);
    }

    private ProjectModel CreateProject(string sourcePath, string analyzerPath)
    {
        var projectPath = Path.Combine(_root, "Sample.csproj");
        var target = new TargetFrameworkModel("net10.0", [], "latest",
            [new ProjectItem("Sample.cs", sourcePath)],
            [new ProjectItem("StyleCop.Analyzers.dll", analyzerPath)],
            [new ProjectItem("stylecop.json", Path.Combine(_root, "stylecop.json"))], []);
        return new ProjectModel("Sample", projectPath, _root, [], [target], "net10.0", false,
            ProjectLoadState.Ready)
        {
            PackageReferences = ["StyleCop.Analyzers"],
        };
    }

    private static string FindStyleCopAnalyzer()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = Directory.Exists(Path.Combine(userProfile, ".nuget", "packages"))
            ? Directory.EnumerateFiles(Path.Combine(userProfile, ".nuget", "packages"), "StyleCop.Analyzers.dll", SearchOption.AllDirectories)
            : [];
        var result = candidates.FirstOrDefault(path => path.Contains("stylecop.analyzers", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result);
        return result;
    }

    private static string FindFixtureRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "CSharpIde");
            if (File.Exists(Path.Combine(candidate, "CSharpIde.sln"))) return candidate;
        }
        throw new DirectoryNotFoundException("CSharpIde fixture がリポジトリ内に見つかりません。");
    }

    private static ProjectModel CreateProjectModel(string projectPath, ProjectEvaluation evaluation, string targetFramework)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        static ProjectItem ToItem(ProjectItemEvaluation item, string directory)
            => new(item.Include, Path.GetFullPath(Path.Combine(directory, item.FullPath ?? item.Include)), item.Link);
        var target = new TargetFrameworkModel(targetFramework,
            (evaluation.DefineConstants ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            evaluation.LangVersion ?? "default",
            evaluation.Compile.Select(item => ToItem(item, directory)).ToArray(),
            evaluation.Analyzers.Select(item => ToItem(item, directory)).ToArray(),
            evaluation.AdditionalFiles.Select(item => ToItem(item, directory)).ToArray(),
            evaluation.None.Select(item => ToItem(item, directory)).ToArray())
        {
            References = (evaluation.References ?? []).Select(item => ToItem(item, directory)).ToArray(),
            Nullable = evaluation.Nullable,
        };
        return new ProjectModel(Path.GetFileNameWithoutExtension(projectPath), Path.GetFullPath(projectPath), directory,
            evaluation.ProjectReferences.Select(item => ToItem(item, directory).FullPath).ToArray(), [target], targetFramework,
            evaluation.IsTestProject, ProjectLoadState.Ready)
        {
            PackageReferences = (evaluation.PackageReferences ?? []).Select(item => item.Include).ToArray(),
        };
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
