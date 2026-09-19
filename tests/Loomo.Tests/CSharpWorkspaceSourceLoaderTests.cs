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

    /// <summary>
    /// ソースを1行も取り込めなかったプロジェクトの名前は登録しない。この名前は
    /// 「その出力 DLL は参照から外す」という意味（ソースと DLL の両方があると CS0436）なので、
    /// 読めていないプロジェクトまで登録すると<b>ソースも DLL も無い</b>状態になり、
    /// そのプロジェクトの型が全部消えて CS0246 が溢れる。
    /// まだ一度もビルドしていない（生成ソースが実在しない）参照先がこれに当たる。
    /// </summary>
    [Fact]
    public void Does_not_claim_projects_whose_sources_could_not_be_read()
    {
        var activePath = Write("Active.cs", "public class Active { }");
        var missingPath = Path.Combine(_root, "NotGenerated.g.cs");   // 実在しない
        var activeProjectPath = Path.Combine(_root, "Active.csproj");
        var missingProjectPath = Path.Combine(_root, "Generated.csproj");
        var generated = Project("Generated", missingProjectPath, [], missingPath);
        var active = Project("Active", activeProjectPath, [missingProjectPath], activePath);
        var solution = new SolutionModel(Path.Combine(_root, "Sample.sln"), "Sample", _root,
            [active, generated], ProjectLoadState.Ready);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, File.ReadAllText(activePath));

        Assert.Contains("Active", snapshot.SourceAssemblyNames);
        Assert.DoesNotContain("Generated", snapshot.SourceAssemblyNames);
    }

    /// <summary>
    /// 読めなかったのが<b>実在しないファイルだけ</b>なら、そのプロジェクトは登録する（＝DLL を外す）。
    /// これは生成ソース（<c>*.g.cs</c>／<c>AssemblyInfo.cs</c>）が未生成という意味で、その型を人が
    /// 参照することは無い。ここで DLL を足すと、読めているソースの型が<b>全部</b>二重になり、
    /// CS0436 が使用箇所の数だけ出る。
    /// </summary>
    [Fact]
    public void Claims_projects_whose_only_unread_files_are_missing_from_disk()
    {
        var activePath = Write("Active.cs", "public class Active { }");
        var readablePath = Write("Readable.cs", "public class Readable { }");
        var missingPath = Path.Combine(_root, "Missing.g.cs");   // 実在しない
        var activeProjectPath = Path.Combine(_root, "Active.csproj");
        var halfProjectPath = Path.Combine(_root, "Half.csproj");
        var half = new ProjectModel("Half", halfProjectPath, _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem("Readable.cs", readablePath),
                new ProjectItem("Missing.g.cs", missingPath),
            ], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var active = Project("Active", activeProjectPath, [halfProjectPath], activePath);
        var solution = new SolutionModel(Path.Combine(_root, "Sample.sln"), "Sample", _root,
            [active, half], ProjectLoadState.Ready);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, File.ReadAllText(activePath));

        Assert.Contains(readablePath, snapshot.Texts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Half", snapshot.SourceAssemblyNames);
        // 読めなかったことは別に数える——意味解析の結果を人へ見せてよいかの判断に使う。
        Assert.Equal(1, snapshot.MissingFileCount);
        Assert.True(snapshot.HasUnreadableSources);
        Assert.True(snapshot.IsComplete);   // 「上限で切り詰めた」わけではない
    }

    /// <summary>
    /// 上限で切り詰められたプロジェクトは登録しない（＝DLL を参照に残す）。読めなかったのは
    /// <b>人の書いたソース</b>で、その型は DLL にしか残っていない。型が二重（CS0436・警告）の方が、
    /// 型が消える（CS0246・エラー）よりずっと後始末が利く。
    /// </summary>
    [Fact]
    public void Does_not_claim_projects_whose_sources_were_truncated()
    {
        var activePath = Write("Active.cs", "public class Active { }");
        var readablePath = Write("Readable.cs", "public class Readable { }");
        // 1ファイルの上限（8MB）を超えるソース＝切り詰められる側。
        var hugePath = Write("Huge.cs", "// " + new string('x', 9 * 1024 * 1024) + "\npublic class Huge { }");
        var activeProjectPath = Path.Combine(_root, "Active.csproj");
        var bulkProjectPath = Path.Combine(_root, "Bulk.csproj");
        var bulk = new ProjectModel("Bulk", bulkProjectPath, _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest", [
                new ProjectItem("Readable.cs", readablePath),
                new ProjectItem("Huge.cs", hugePath),
            ], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        var active = Project("Active", activeProjectPath, [bulkProjectPath], activePath);
        var solution = new SolutionModel(Path.Combine(_root, "Sample.sln"), "Sample", _root,
            [active, bulk], ProjectLoadState.Ready);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, File.ReadAllText(activePath));

        Assert.DoesNotContain("Bulk", snapshot.SourceAssemblyNames);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public void Claims_projects_whose_sources_were_read()
    {
        var contractsPath = Write("Contracts.cs", "public interface IContract { }");
        var featurePath = Write("Feature.cs", "public class Feature { }");
        var contractsProjectPath = Path.Combine(_root, "Contracts.csproj");
        var featureProjectPath = Path.Combine(_root, "Feature.csproj");
        var contracts = Project("Contracts", contractsProjectPath, [], contractsPath);
        var feature = Project("Feature", featureProjectPath, [contractsProjectPath], featurePath);
        var solution = new SolutionModel(Path.Combine(_root, "Sample.sln"), "Sample", _root,
            [feature, contracts], ProjectLoadState.Ready);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, featurePath, File.ReadAllText(featurePath));

        // 辿った ProjectReference 先も、ソースを積めている限り名前を登録する（DLL と二重にしない）。
        Assert.Contains("Feature", snapshot.SourceAssemblyNames);
        Assert.Contains("Contracts", snapshot.SourceAssemblyNames);
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
