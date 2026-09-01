using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpWorkspaceOperationContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "LoomoCSharpOperationContext_" + Guid.NewGuid().ToString("N"));

    public CSharpWorkspaceOperationContextTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Creates_a_syntax_context_without_starting_a_semantic_compilation()
    {
        var sourcePath = Write("Feature.cs", "public class Feature { }");
        var projectPath = Path.Combine(_root, "Feature.csproj");
        var solution = Solution(projectPath, sourcePath);
        const string unsavedText = "public class Feature { public int Value; }";

        var context = CSharpWorkspaceOperationContext.Create(solution, sourcePath, unsavedText);

        Assert.Equal(unsavedText, context.Snapshot.Texts[sourcePath]);
        Assert.Null(context.SemanticCompilation);
        Assert.Contains(sourcePath, context.Snapshot.ParseOptionsByPath.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(context.IsSourceSnapshotComplete);
        Assert.Null(context.SourceSnapshotWarning);
    }

    [Fact]
    public void Exposes_a_warning_when_the_source_snapshot_is_incomplete()
    {
        var snapshot = new CSharpWorkspaceSourceSnapshot(
            new Dictionary<string, string>(),
            new Dictionary<string, CSharpParseOptions>(),
            SkippedFileCount: 3);
        var context = new CSharpWorkspaceOperationContext(snapshot, null);

        Assert.False(context.IsSourceSnapshotComplete);
        Assert.Equal("C#ソースが上限で切り詰められています（3ファイル）。",
            context.SourceSnapshotWarning);
    }

    [Fact]
    public void Creates_semantic_compilation_from_the_same_unsaved_snapshot()
    {
        var sourcePath = Write("Feature.cs", "public class Feature { }");
        var projectPath = Path.Combine(_root, "Feature.csproj");
        var solution = Solution(projectPath, sourcePath);
        const string unsavedText = "public class Feature { public int Value; }";

        var context = CSharpWorkspaceOperationContext.Create(
            solution, sourcePath, unsavedText,
            includeSemanticCompilation: true);

        Assert.NotNull(context.SemanticCompilation);
        Assert.Contains(context.SemanticCompilation!.SyntaxTrees,
            tree => string.Equals(Path.GetFullPath(tree.FilePath!), sourcePath,
                StringComparison.OrdinalIgnoreCase) && tree.GetText().ToString() == unsavedText);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static SolutionModel Solution(string projectPath, string sourcePath)
        => new(null, "Feature", Path.GetDirectoryName(projectPath)!,
            [new ProjectModel("Feature", projectPath, Path.GetDirectoryName(projectPath)!, [],
                [new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem(Path.GetFileName(sourcePath), sourcePath)], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready)], ProjectLoadState.Ready);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
