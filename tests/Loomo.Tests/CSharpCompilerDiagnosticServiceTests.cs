using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using LspDiagnosticSeverity = Editor.Core.Lsp.DiagnosticSeverity;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpCompilerDiagnosticServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "LoomoCompilerDiagnostics_" + Guid.NewGuid().ToString("N"));

    public CSharpCompilerDiagnosticServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Reports_unsaved_syntax_diagnostic_for_the_active_file()
    {
        var path = Path.Combine(_root, "Broken.cs");
        File.WriteAllText(path, "class Broken { }");
        var solution = CreateSolution(path);

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
            solution, path, "class Broken { void Run() { int value = 1 } }");

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "CS1002");
        Assert.Equal("Compiler", diagnostic.Source);
        Assert.Equal(LspDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Returns_no_diagnostic_for_valid_unsaved_source()
    {
        var path = Path.Combine(_root, "Valid.cs");
        File.WriteAllText(path, "class Valid { }");

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
            CreateSolution(path), path, "class Valid { void Run() { int value = 1; } }");

        Assert.Null(result.Error);
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == LspDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Applies_editorconfig_compiler_severity_to_the_fallback()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.CS0168.severity = error\n");
        var path = Path.Combine(_root, "Severity.cs");
        File.WriteAllText(path, "class Severity { void Run() { int unused; } }");

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
            CreateSolution(path), path, "class Severity { void Run() { int unused; } }");

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "CS0168");
        Assert.Equal(LspDiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task Suppresses_a_compiler_diagnostic_when_editorconfig_sets_none()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.CS0168.severity = none\n");
        var path = Path.Combine(_root, "Suppressed.cs");
        File.WriteAllText(path, "class Suppressed { void Run() { int unused; } }");

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
            CreateSolution(path), path, "class Suppressed { void Run() { int unused; } }");

        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "CS0168");
    }

    [Fact]
    public async Task Honors_cancellation_before_building_the_compilation()
    {
        var path = Path.Combine(_root, "Cancelled.cs");
        File.WriteAllText(path, "class Cancelled { }");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CSharpCompilerDiagnosticService().AnalyzeAsync(
                CreateSolution(path), path, "class Cancelled { }", cancellation.Token));
    }

    [Fact]
    public async Task Uses_the_selected_target_framework_parse_options()
    {
        var path = Path.Combine(_root, "Preview.cs");
        File.WriteAllText(path, "class Preview { }");
        var target = new TargetFrameworkModel("net10.0", [], "preview",
            [new ProjectItem("Preview.cs", path)], [], [], []);
        var project = new ProjectModel("Preview", Path.Combine(_root, "Preview.csproj"),
            _root, [], [target], "net10.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(null, "Preview", _root, [project], ProjectLoadState.Ready);

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
            solution, path, "class Preview { void Run() { int[] value = [1, 2, 3]; } }");

        Assert.Null(result.Error);
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Uses_unsaved_text_for_other_open_compile_files()
    {
        var activePath = Path.Combine(_root, "Active.cs");
        var dependencyPath = Path.Combine(_root, "Dependency.cs");
        File.WriteAllText(activePath,
            "class Active { int Run() => new Dependency().Value; }");
        File.WriteAllText(dependencyPath, "class Dependency { }");
        var project = new ProjectModel("Sample", Path.Combine(_root, "Sample.csproj"),
            _root, [], [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Active.cs", activePath), new ProjectItem("Dependency.cs", dependencyPath)],
                [], [], [])], "net10.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(null, "Sample", _root, [project], ProjectLoadState.Ready);

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
            solution, activePath, File.ReadAllText(activePath),
            openTexts: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [dependencyPath] = "class Dependency { public int Value => 42; }",
            });

        Assert.Null(result.Error);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code is "CS1061" or "CS0117");
    }

    private SolutionModel CreateSolution(string path)
    {
        var project = new ProjectModel("Sample", Path.Combine(_root, "Sample.csproj"),
            _root, [], [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem(Path.GetFileName(path), path)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(null, "Sample", _root, [project], ProjectLoadState.Ready);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
