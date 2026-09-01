using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpFixAllPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "LoomoFixAllPlan_" + Guid.NewGuid().ToString("N"));

    public CSharpFixAllPlannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Selects_only_existing_CSharp_compile_files_in_the_requested_scope()
    {
        var first = Write("First.cs");
        var second = Write("Second.cs");
        var appPath = Write("App.csproj");
        var libraryPath = Write("Library.csproj");
        var app = Project(appPath, "App", [first]);
        var library = Project(libraryPath, "Library", [second]);
        var solution = new SolutionModel(Path.Combine(_root, "App.sln"), "App", _root,
            [app, library], ProjectLoadState.Ready);

        var projectPlan = CSharpFixAllPlanner.Create(solution, appPath, CSharpFixAllScope.Project);
        Assert.True(projectPlan.IsValid);
        Assert.Single(projectPlan.Projects);
        Assert.Equal(first, Assert.Single(projectPlan.Files));

        var solutionPlan = CSharpFixAllPlanner.Create(solution, appPath, CSharpFixAllScope.Solution);
        Assert.True(solutionPlan.IsValid);
        Assert.Equal(2, solutionPlan.Projects.Count);
        Assert.Equal([first, second], solutionPlan.Files);
    }

    [Fact]
    public void Document_scope_contains_only_the_current_compile_file()
    {
        var first = Write("First.cs");
        var second = Write("Second.cs");
        var appPath = Write("App.csproj");
        var app = Project(appPath, "App", [first, second]);
        var solution = new SolutionModel(Path.Combine(_root, "App.sln"), "App", _root,
            [app], ProjectLoadState.Ready);

        var plan = CSharpFixAllPlanner.CreateForDocument(solution, first);

        Assert.True(plan.IsValid, plan.Error);
        Assert.Same(app, Assert.Single(plan.Projects));
        Assert.Equal([first], plan.Files);
    }

    [Fact]
    public void Document_scope_rejects_a_file_outside_the_selected_project()
    {
        var source = Write("First.cs");
        var outside = Path.Combine(_root, "Outside.cs");
        File.WriteAllText(outside, "class Outside { }");
        var appPath = Write("App.csproj");
        var solution = new SolutionModel(null, "App", _root,
            [Project(appPath, "App", [source])], ProjectLoadState.Ready);

        var plan = CSharpFixAllPlanner.CreateForDocument(solution, outside);

        Assert.False(plan.IsValid);
        Assert.Contains("プロジェクト", plan.Error);
    }

    [Fact]
    public void Rejects_a_missing_or_non_ready_project_before_querying_LSP()
    {
        var projectPath = Write("App.csproj");
        var project = Project(projectPath, "App", [], ProjectLoadState.Failed);
        var solution = new SolutionModel(null, "App", _root, [project], ProjectLoadState.Ready);

        var result = CSharpFixAllPlanner.Create(solution, projectPath, CSharpFixAllScope.Project);

        Assert.False(result.IsValid);
        Assert.Contains("まだ読み込まれていません", result.Error);
        Assert.Empty(CSharpFixAllPlanner.Create(solution, Write("Missing.csproj"),
            CSharpFixAllScope.Project).Projects);
    }

    [Fact]
    public void Merges_identical_linked_file_edits_but_rejects_conflicts()
    {
        var destination = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        var uri = "file:///shared/Linked.cs";
        var edit = new LspTextEdit(new LspRange(new(0, 0), new(0, 1)), "x");

        Assert.Null(CSharpFixAllEditMerger.Merge(destination,
            new Dictionary<string, IReadOnlyList<LspTextEdit>> { [uri] = [edit] }));
        Assert.Null(CSharpFixAllEditMerger.Merge(destination,
            new Dictionary<string, IReadOnlyList<LspTextEdit>> { [uri] = [edit] }));
        Assert.Equal([edit], destination[uri]);

        var error = CSharpFixAllEditMerger.Merge(destination,
            new Dictionary<string, IReadOnlyList<LspTextEdit>>
            {
                [uri] = [new LspTextEdit(edit.Range, "different")],
            });
        Assert.Contains("linked file", error);
    }

    [Fact]
    public void Does_not_partially_merge_when_a_later_linked_file_conflicts()
    {
        var destination = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        var existingUri = "file:///shared/Existing.cs";
        var newUri = "file:///shared/New.cs";
        var existing = new LspTextEdit(new LspRange(new(0, 0), new(0, 1)), "old");
        destination[existingUri] = [existing];

        var error = CSharpFixAllEditMerger.Merge(destination,
            new Dictionary<string, IReadOnlyList<LspTextEdit>>
            {
                [newUri] = [new LspTextEdit(new LspRange(new(0, 0), new(0, 0)), "new")],
                [existingUri] = [new LspTextEdit(existing.Range, "different")],
            });

        Assert.Contains("linked file", error);
        Assert.Equal([existing], destination[existingUri]);
        Assert.DoesNotContain(newUri, destination.Keys);
    }

    private string Write(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static ProjectModel Project(
        string path, string name, IReadOnlyList<string> files,
        ProjectLoadState state = ProjectLoadState.Ready)
    {
        var target = new TargetFrameworkModel("net10.0", [], "latest",
            files.Select(file => new ProjectItem(Path.GetFileName(file), file)).ToArray(),
            [], [], []);
        return new ProjectModel(name, path, Path.GetDirectoryName(path)!, [], [target],
            "net10.0", false, state);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
