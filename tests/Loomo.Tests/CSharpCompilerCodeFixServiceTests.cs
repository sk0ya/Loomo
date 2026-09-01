using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpCompilerCodeFixServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "LoomoCompilerCodeFixes_" + Guid.NewGuid().ToString("N"));

    public CSharpCompilerCodeFixServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Offers_a_using_fix_only_when_the_candidate_resolves_the_diagnostic()
    {
        var path = Write("MissingUsing.cs", "class Sample { Console Value; }\n");
        var source = await File.ReadAllTextAsync(path);

        var actions = await CSharpCompilerCodeFixService.GetAsync(
            CreateSolution(path), path, source,
            new LspRange(new(0, 0), new(0, source.Length)));

        var action = Assert.Single(actions, item => item.Title == "using System を追加");
        var edit = Assert.Single(action.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Equal("using System;\n", edit.NewText);
        Assert.Equal(0, edit.Range.Start.Line);
        Assert.Equal(0, edit.Range.Start.Character);
    }

    [Fact]
    public async Task Offers_a_targeted_remove_action_for_an_unused_using()
    {
        var path = Write("UnusedUsing.cs",
            "using System;\nusing System.Text;\n\nclass Sample { void Run() => Console.WriteLine(\"ok\"); }\n");
        var source = await File.ReadAllTextAsync(path);

        var actions = await CSharpCompilerCodeFixService.GetAsync(
            CreateSolution(path), path, source,
            new LspRange(new(0, 0), new(3, 20)));

        var action = Assert.Single(actions, item => item.Title == "未使用のusingを削除");
        var edit = Assert.Single(action.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Equal("using System.Text;\n", source[ToOffset(source, edit.Range.Start)..ToOffset(source, edit.Range.End)]);
        Assert.Equal(string.Empty, edit.NewText);
    }

    [Fact]
    public async Task Removes_only_a_single_plain_unused_local_declaration()
    {
        var path = Write("UnusedLocal.cs",
            "class Sample { void Run() { int unused; } }\n");
        var source = await File.ReadAllTextAsync(path);

        var actions = await CSharpCompilerCodeFixService.GetAsync(
            CreateSolution(path), path, source,
            new LspRange(new(0, 0), new(0, source.Length)));

        var action = Assert.Single(actions, item => item.Title == "未使用のローカル変数を削除");
        var edit = Assert.Single(action.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Equal("int unused;", source[ToOffset(source, edit.Range.Start)..ToOffset(source, edit.Range.End)]);
        Assert.Equal(string.Empty, edit.NewText);
    }

    [Fact]
    public async Task Fixes_compiler_diagnostics_across_all_requested_files()
    {
        var unusedUsingPath = Write("UnusedUsing.cs", "using System.Text;\nclass One { }\n");
        var unusedLocalPath = Write("UnusedLocal.cs",
            "class Two { void Run() { int unused; } }\n");
        var solution = CreateSolution(unusedUsingPath, unusedLocalPath);

        var result = await CSharpCompilerCodeFixService.ApplyAllAsync(
            solution, [unusedUsingPath, unusedLocalPath]);

        Assert.Null(result.Error);
        Assert.True(result.ActionsFound >= 2);
        Assert.Equal(2, result.Edit!.Changes.Count);
        var usingEdit = Assert.Single(result.Edit.Changes[LspUri.FromPath(unusedUsingPath)]);
        var localEdit = Assert.Single(result.Edit.Changes[LspUri.FromPath(unusedLocalPath)]);
        Assert.DoesNotContain("using System.Text;", usingEdit.NewText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("int unused;", localEdit.NewText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unified_fix_all_does_not_require_stylecop_to_fix_compiler_diagnostics()
    {
        var path = Write("NoAnalyzer.cs", "using System.Text;\nclass Sample { }\n");
        var solution = CreateSolution(path);
        var plan = CSharpFixAllPlanner.Create(
            solution, Path.Combine(_root, "Sample.csproj"), CSharpFixAllScope.Project);

        var result = await CSharpFixAllService.ApplyAsync(solution, plan);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values).Single();
        Assert.DoesNotContain("using System.Text;", edit.NewText,
            StringComparison.Ordinal);
        Assert.Equal("using System.Text;\nclass Sample { }\n",
            result.ExpectedTexts![path]);
        Assert.Equal(result.ExpectedTexts[path],
            result.Edit.ExpectedTexts![Path.GetFullPath(path)]);
    }

    [Fact]
    public async Task Unified_fix_all_document_scope_does_not_edit_sibling_compile_files()
    {
        var first = Write("DocumentScope.cs", "using System.Text;\nclass First { }\n");
        var second = Write("Sibling.cs", "using System.Text;\nclass Second { }\n");
        var solution = CreateSolution(first, second);
        var plan = CSharpFixAllPlanner.CreateForDocument(solution, first);

        var result = await CSharpFixAllService.ApplyAsync(solution, plan);

        Assert.Null(result.Error);
        Assert.Equal(1, result.DocumentsScanned);
        Assert.Single(result.Edit!.Changes);
        Assert.Contains(LspUri.FromPath(first), result.Edit.Changes.Keys);
        Assert.DoesNotContain(LspUri.FromPath(second), result.Edit.Changes.Keys);
        Assert.DoesNotContain("using System.Text;",
            result.Edit.Changes[LspUri.FromPath(first)].Single().NewText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unified_solution_fix_all_merges_identical_stylecop_edits_for_a_linked_file()
    {
        var sharedPath = Write("Shared.cs",
            "public sealed class Shared { private int value; public int Get() => value; }");
        File.WriteAllText(Path.Combine(_root, ".editorconfig"),
            "root = true\n[*.cs]\ndotnet_diagnostic.SA1101.severity = error\n");
        var analyzerPath = FindStyleCopAnalyzer();
        var first = CreateProjectWithAnalyzer("First", sharedPath, analyzerPath);
        var second = CreateProjectWithAnalyzer("Second", sharedPath, analyzerPath);
        var solution = new SolutionModel(null, "Shared", _root, [first, second],
            ProjectLoadState.Ready);
        var plan = CSharpFixAllPlanner.Create(solution, first.FullPath,
            CSharpFixAllScope.Solution);

        var result = await CSharpFixAllService.ApplyAsync(solution, plan);

        Assert.Null(result.Error);
        Assert.True(result.ActionsFound >= 2);
        var edit = Assert.Single(result.Edit!.Changes[LspUri.FromPath(sharedPath)]);
        Assert.Contains("this.value", edit.NewText, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(sharedPath), result.ExpectedTexts![sharedPath]);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private SolutionModel CreateSolution(params string[] sourcePaths)
    {
        var project = new ProjectModel("Sample", Path.Combine(_root, "Sample.csproj"), _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                sourcePaths.Select(path => new ProjectItem(Path.GetFileName(path), path)).ToArray(),
                [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(null, "Sample", _root, [project], ProjectLoadState.Ready);
    }

    private ProjectModel CreateProjectWithAnalyzer(
        string name, string sourcePath, string analyzerPath)
    {
        var projectPath = Path.Combine(_root, name + ".csproj");
        var target = new TargetFrameworkModel("net10.0", [], "latest",
            [new ProjectItem(Path.GetFileName(sourcePath), sourcePath)],
            [new ProjectItem("StyleCop.Analyzers.dll", analyzerPath)], [], []);
        return new ProjectModel(name, projectPath, _root, [], [target], "net10.0", false,
            ProjectLoadState.Ready)
        {
            PackageReferences = ["StyleCop.Analyzers"],
        };
    }

    private static string FindStyleCopAnalyzer()
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var result = Directory.EnumerateFiles(packages, "StyleCop.Analyzers.dll",
                SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains("stylecop.analyzers",
                StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result);
        return result;
    }

    private static int ToOffset(string source, LspPosition position)
    {
        var lines = source.Split("\n", StringSplitOptions.None);
        return lines.Take(position.Line).Sum(line => line.Length + 1) + position.Character;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
