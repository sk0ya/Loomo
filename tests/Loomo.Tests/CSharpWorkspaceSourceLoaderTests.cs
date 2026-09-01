using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpWorkspaceSourceLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "LoomoCSharpSources_" + Guid.NewGuid().ToString("N"));

    public CSharpWorkspaceSourceLoaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Loads_the_active_project_and_transitive_project_references()
    {
        var contractsPath = Write("Contracts.cs", "public interface IContract { void Run(); }");
        var featurePath = Write("Feature.cs", "class Feature : IContract { }");
        var contractsProjectPath = Path.Combine(_root, "Contracts.csproj");
        var featureProjectPath = Path.Combine(_root, "Feature.csproj");
        var contracts = Project("Contracts", contractsProjectPath, [], contractsPath);
        var feature = Project("Feature", featureProjectPath, [contractsProjectPath], featurePath);
        var solution = new SolutionModel(Path.Combine(_root, "Sample.sln"), "Sample", _root,
            [feature, contracts], ProjectLoadState.Ready);

        const string activeText = """
            public sealed class Feature : IContract
            {
            }
            """;
        var result = CSharpWorkspaceSourceLoader.Load(solution, featurePath, activeText);

        Assert.Equal(activeText, result[featurePath]);
        Assert.Equal("public interface IContract { void Run(); }", result[contractsPath]);

        var generated = CSharpCodeGenerationService.Generate(
            featurePath, result[featurePath], 0, result[featurePath].IndexOf("Feature", StringComparison.Ordinal),
            CSharpCodeGenerationKind.ImplementInterface, result);
        Assert.Null(generated.Error);
        Assert.Contains("void Run()", generated.Edit!.Changes.Values.SelectMany(edits => edits)
            .Single().NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_unsaved_text_for_other_open_compile_files()
    {
        var activePath = Write("Active.cs", "public class Active { }");
        var otherPath = Write("Other.cs", "public class Other { public int Value => 1; }");
        var projectPath = Path.Combine(_root, "OpenBuffers.csproj");
        var project = new ProjectModel("OpenBuffers", projectPath, _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem("Active.cs", activePath),
                new ProjectItem("Other.cs", otherPath),
            ], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(null, "OpenBuffers", _root, [project], ProjectLoadState.Ready);
        const string unsavedOther = "public class Other { public string Value => \"unsaved\"; }";

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, File.ReadAllText(activePath),
            openTexts: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [otherPath] = unsavedOther,
            });

        Assert.Equal(unsavedOther, snapshot.Texts[otherPath]);
    }

    [Fact]
    public void Code_generation_uses_the_selected_target_framework_symbols_for_references()
    {
        var contractsPath = Write("ConditionalContracts.cs", "#if FEATURE_CONTRACT\npublic interface IContract { void Run(); }\n#endif");
        var featurePath = Write("ConditionalFeature.cs", "class Feature : IContract\n{\n}\n");
        var contractsProjectPath = Path.Combine(_root, "ConditionalContracts.csproj");
        var featureProjectPath = Path.Combine(_root, "ConditionalFeature.csproj");
        var contracts = Project("Contracts", contractsProjectPath, [], contractsPath, ["FEATURE_CONTRACT"]);
        var feature = Project("Feature", featureProjectPath, [contractsProjectPath], featurePath);
        var solution = new SolutionModel(Path.Combine(_root, "Conditional.sln"), "Conditional", _root,
            [feature, contracts], ProjectLoadState.Ready);
        var activeText = File.ReadAllText(featurePath);
        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(solution, featurePath, activeText);
        var options = new CSharpGenerationOptions(
            ParseOptions: CSharpWorkspaceSourceLoader.ParseOptionsForFile(solution, featurePath),
            WorkspaceParseOptions: snapshot.ParseOptionsByPath);

        var result = CSharpCodeGenerationService.Generate(
            featurePath, activeText, 0, activeText.IndexOf("Feature", StringComparison.Ordinal),
            CSharpCodeGenerationKind.ImplementInterface, snapshot.Texts, options);

        Assert.Null(result.Error);
        Assert.Contains("void Run()", result.Edit!.Changes.Values.SelectMany(edits => edits)
            .Single().NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_selected_target_framework_project_references_instead_of_global_fallback()
    {
        var referencedPath = Write("ConditionalReference.cs", "public class Referenced { }");
        var activePath = Write("ConditionalActive.cs", "public class Active { }");
        var referencedProjectPath = Path.Combine(_root, "Referenced.csproj");
        var activeProjectPath = Path.Combine(_root, "Active.csproj");

        var referenced = Project("Referenced", referencedProjectPath, [], referencedPath);
        var active = new ProjectModel("Active", activeProjectPath, _root,
            [referencedProjectPath],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("ConditionalActive.cs", activePath)], [], [], [])
            {
                ProjectReferences = [],
            }],
            "net10.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(null, "Conditional", _root,
            [active, referenced], ProjectLoadState.Ready);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, File.ReadAllText(activePath));

        Assert.Contains(activePath, snapshot.Texts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(referencedPath, snapshot.Texts.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Solution_scope_includes_reverse_reference_projects_for_workspace_operations()
    {
        var basePath = Write("Base.cs", "public class Base { }");
        var derivedPath = Write("Derived.cs", "public class Derived : Base { }");
        var baseProjectPath = Path.Combine(_root, "Base.csproj");
        var derivedProjectPath = Path.Combine(_root, "Derived.csproj");
        var baseProject = Project("Base", baseProjectPath, [], basePath);
        var derivedProject = Project("Derived", derivedProjectPath, [baseProjectPath], derivedPath);
        var solution = new SolutionModel(Path.Combine(_root, "Graph.sln"), "Graph", _root,
            [baseProject, derivedProject], ProjectLoadState.Ready);

        var graph = CSharpWorkspaceSourceLoader.Load(solution, basePath, File.ReadAllText(basePath));
        var all = CSharpWorkspaceSourceLoader.Load(solution, basePath, File.ReadAllText(basePath),
            CSharpWorkspaceSourceScope.Solution);

        Assert.DoesNotContain(derivedPath, graph.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(derivedPath, all.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bounds_a_large_snapshot_but_keeps_the_unsaved_active_document()
    {
        var sourceItems = Enumerable.Range(0, CSharpWorkspaceSourceLoader.MaxSourceFileCount + 8)
            .Select(index =>
            {
                var path = Write($"Bulk{index:D4}.cs", $"class Bulk{index} {{ }}");
                return new ProjectItem(Path.GetFileName(path), path);
            })
            .ToList();
        var activePath = Write("Active.cs", "class Active { }");
        sourceItems.Add(new ProjectItem("Active.cs", activePath));
        var projectPath = Path.Combine(_root, "Bulk.csproj");
        var project = new ProjectModel("Bulk", projectPath, _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest", sourceItems, [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(null, "Bulk", _root, [project], ProjectLoadState.Ready);

        const string unsaved = "class Active { int Unsaved => 42; }";
        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(solution, activePath, unsaved);

        Assert.Equal(unsaved, snapshot.Texts[activePath]);
        Assert.Contains(Path.Combine(_root, "Bulk0000.cs"), snapshot.Texts.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(_root, $"Bulk{CSharpWorkspaceSourceLoader.MaxSourceFileCount:D4}.cs"),
            snapshot.Texts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CSharpWorkspaceSourceLoader.MaxSourceFileCount + 1, snapshot.Texts.Count);
        Assert.Equal(8, snapshot.SkippedFileCount);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public void Workspace_refactoring_uses_each_project_parse_options()
    {
        var baseText = """
            public class Base
            {
                public void Move()
                {
                }
            }
            """;
        var derivedText = "#if FEATURE_CHILD\npublic class Derived : Base\n{\n}\n#endif\n";
        var basePath = Write("ConditionalBase.cs", baseText);
        var derivedPath = Write("ConditionalDerived.cs", derivedText);
        var baseProjectPath = Path.Combine(_root, "ConditionalBase.csproj");
        var derivedProjectPath = Path.Combine(_root, "ConditionalDerived.csproj");
        var baseProject = Project("Base", baseProjectPath, [], basePath);
        var derivedProject = Project("Derived", derivedProjectPath, [baseProjectPath], derivedPath,
            ["FEATURE_CHILD"]);
        var solution = new SolutionModel(Path.Combine(_root, "ConditionalGraph.sln"), "ConditionalGraph", _root,
            [baseProject, derivedProject], ProjectLoadState.Ready);
        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, basePath, baseText, CSharpWorkspaceSourceScope.Solution);
        var start = baseText.IndexOf("Move", StringComparison.Ordinal);
        var selection = new LspRange(Position(baseText, start), Position(baseText, start + "Move".Length));

        var result = CSharpPushDownMemberService.PushDown(
            basePath, baseText, selection, snapshot.Texts,
            destinationPath: derivedPath, workspaceParseOptions: snapshot.ParseOptionsByPath);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Edit!.Changes.Count);
        Assert.Contains("Move", result.Edit.Changes[LspUri.FromPath(derivedPath)].Single().NewText);
    }

    [Fact]
    public void Solution_scope_prefers_active_project_parse_options_for_linked_files()
    {
        var linkedPath = Write("Linked.cs", "#if ACTIVE_PROJECT\nclass Linked { }\n#endif\n");
        var activePath = Write("Active.cs", "class Active { }\n");
        var otherProjectPath = Path.Combine(_root, "Other.csproj");
        var activeProjectPath = Path.Combine(_root, "Active.csproj");
        var other = new ProjectModel("Other", otherProjectPath, _root, [],
            [new TargetFrameworkModel("net10.0", ["OTHER_PROJECT"], "latest",
                [new ProjectItem("Linked.cs", linkedPath)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var active = new ProjectModel("Active", activeProjectPath, _root, [],
            [new TargetFrameworkModel("net10.0", ["ACTIVE_PROJECT"], "latest",
                [new ProjectItem("Active.cs", activePath), new ProjectItem("Linked.cs", linkedPath)],
                [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var solution = new SolutionModel(Path.Combine(_root, "Linked.sln"), "Linked", _root,
            [other, active], ProjectLoadState.Ready);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, File.ReadAllText(activePath), CSharpWorkspaceSourceScope.Solution);

        Assert.Contains("ACTIVE_PROJECT", snapshot.ParseOptionsByPath[linkedPath]
            .PreprocessorSymbolNames, StringComparer.Ordinal);
        Assert.DoesNotContain("OTHER_PROJECT", snapshot.ParseOptionsByPath[linkedPath]
            .PreprocessorSymbolNames, StringComparer.Ordinal);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static LspPosition Position(string text, int offset)
    {
        var lines = text[..offset].Split('\n');
        return new LspPosition(lines.Length - 1, lines[^1].Length);
    }

    private static ProjectModel Project(string name, string projectPath,
        IReadOnlyList<string> references, string sourcePath,
        IReadOnlyList<string>? defineConstants = null)
        => new(name, projectPath, Path.GetDirectoryName(projectPath)!, references,
            [new TargetFrameworkModel("net10.0", defineConstants ?? [], "latest",
                [new ProjectItem(Path.GetFileName(sourcePath), sourcePath)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
