using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
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
    public void Does_not_reference_the_compilation_assembly_itself()
    {
        var path = Path.Combine(_root, "PaneKind.cs");
        var source = "namespace sk0ya.Loomo.App.Services; public enum PaneKind { Source }";

        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source },
            referencePaths: [typeof(PaneKind).Assembly.Location],
            assemblyName: typeof(PaneKind).Assembly.GetName().Name);

        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0436");
    }

    [Fact]
    public void Does_not_reference_the_running_application_assemblies()
    {
        // 走っているプロセス（Loomo 本体／テストホスト）は自分の出力に sk0ya.Loomo.Services.dll を
        // 抱えている。それを参照へ混ぜると、同じ型を持つソースと衝突して CS0436 になる。
        var path = Path.Combine(_root, "SettingsStore.cs");
        var source = """
            namespace sk0ya.Loomo.Services.Settings;
            public class SettingsStore { }
            public static class Caller { public static SettingsStore Make() => new SettingsStore(); }
            """;

        // 本番と同じ形——MSBuild 参照は空で、assemblyName は csproj のファイル名（実アセンブリ名と違う）。
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source },
            assemblyName: "Loomo.Services");

        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0436");
        var hostDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        Assert.DoesNotContain(compilation.References, reference => string.Equals(
            Path.GetDirectoryName(reference.Display ?? ""), hostDirectory, StringComparison.OrdinalIgnoreCase));
        // 標準ライブラリは残る（共有フレームワークは実行中アプリの出力ではない）。
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0518");
    }

    [Fact]
    public void Uses_only_the_msbuild_resolved_references_when_they_exist()
    {
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [Path.Combine(_root, "A.cs")] = "class A { }" },
            referencePaths: [typeof(object).Assembly.Location]);

        // MSBuild が参照を返したら、それが参照の全部。実行中プロセスの都合を足さない。
        var reference = Assert.Single(compilation.References);
        Assert.Equal(typeof(object).Assembly.Location, reference.Display, ignoreCase: true);
    }

    [Fact]
    public void Compilation_assembly_name_prefers_the_evaluated_assembly_name()
    {
        var project = new ProjectModel("Loomo.Services", Path.Combine(_root, "Loomo.Services.csproj"),
            _root, [], [], null, false, ProjectLoadState.Ready);

        Assert.Equal("Loomo.Services", project.CompilationAssemblyName);
        Assert.Equal("sk0ya.Loomo.Services",
            (project with { AssemblyName = "sk0ya.Loomo.Services" }).CompilationAssemblyName);
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

    [Fact]
    public async Task Does_not_reference_the_output_of_projects_whose_source_is_in_the_compilation()
    {
        // 本番でずっと出ていた形——Loomo.App のファイルを開くと、ProjectReference 先である
        // Loomo.Core の「ソース」が Compilation へ積まれる一方、MSBuild の @(ReferencePath) には
        // 同じ Loomo.Core の「出力 DLL」が並ぶ。両方入ると全型が二重に見えて CS0436 になる。
        // 「自分自身の DLL だけ外す」では防げない（衝突するのは参照先であって自分ではない）。
        var settingsPath = Path.Combine(_root, "LoomoSettings.cs");
        File.WriteAllText(settingsPath, """
            namespace sk0ya.Loomo.Core.Settings;
            public class LoomoSettings { public int MaxTokens { get; set; } }
            """);
        var appPath = Path.Combine(_root, "Reader.cs");
        var appSource = """
            using sk0ya.Loomo.Core.Settings;
            public class Reader { public int Read(LoomoSettings settings) => settings.MaxTokens; }
            """;
        File.WriteAllText(appPath, appSource);

        var corePath = Path.Combine(_root, "Loomo.Core.csproj");
        var core = new ProjectModel("Loomo.Core", corePath, _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("LoomoSettings.cs", settingsPath)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready)
        { AssemblyName = "sk0ya.Loomo.Core" };
        var app = new ProjectModel("Loomo.App", Path.Combine(_root, "Loomo.App.csproj"), _root, [corePath],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Reader.cs", appPath)], [], [], [])
            {
                ProjectReferences = [corePath],
                References = [new ProjectItem("sk0ya.Loomo.Core.dll",
                    typeof(Core.Settings.LoomoSettings).Assembly.Location)],
            }],
            "net10.0", false, ProjectLoadState.Ready)
        { AssemblyName = "sk0ya.Loomo.App" };
        var solution = new SolutionModel(null, "Loomo", _root, [app, core], ProjectLoadState.Ready);

        var result = await new CSharpCompilerDiagnosticService().AnalyzeAsync(solution, appPath, appSource);

        Assert.Null(result.Error);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "CS0436");
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
