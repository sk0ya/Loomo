using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.CSharp.Projects;

public enum CSharpSolutionNodeKind
{
    Solution,
    Project,
    TargetFramework,
    ProjectReference,
    Folder,
    File,
    Analyzer,
    AdditionalFile,
    NoneFile,
}

/// <summary>Solution Explorerへ渡す、ファイルシステムとは独立したC#プロジェクト階層。</summary>
public sealed record CSharpSolutionNode(
    string Name,
    CSharpSolutionNodeKind Kind,
    string? FullPath,
    IReadOnlyList<CSharpSolutionNode> Children,
    bool IsSelected = false,
    bool CanRunTests = false);

/// <summary>評価済みの <see cref="SolutionModel"/> を solution／project／TFM／folder／file の
/// 表示階層へ変換する。表示側はMSBuild XMLや相対パスを再解釈しない。</summary>
public static class CSharpSolutionTreeBuilder
{
    public static CSharpSolutionNode Build(SolutionModel solution)
        => new(solution.Name, CSharpSolutionNodeKind.Solution, solution.FullPath,
            solution.Projects.Select(BuildProject).ToList(),
            CanRunTests: solution.Projects.Any(project => project.IsTestProject));

    private static CSharpSolutionNode BuildProject(ProjectModel project)
    {
        var children = new List<CSharpSolutionNode>();
        var projectReferences = project.SelectedTargetFrameworkModel?.ProjectReferences
            ?? project.ProjectReferences;
        if (projectReferences.Count > 0)
        {
            children.Add(new CSharpSolutionNode("参照", CSharpSolutionNodeKind.ProjectReference, null,
                projectReferences
                    .Select(path => new CSharpSolutionNode(
                        Path.GetFileNameWithoutExtension(path), CSharpSolutionNodeKind.ProjectReference, path, []))
                    .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        foreach (var tfm in project.TargetFrameworks)
        {
            var tfmChildren = new List<CSharpSolutionNode>();
            AddFileTree(tfmChildren, project.Directory, tfm.CompileFiles, CSharpSolutionNodeKind.File);
            AddItemGroup(tfmChildren, "Analyzer", CSharpSolutionNodeKind.Analyzer, tfm.Analyzers);
            AddItemGroup(tfmChildren, "Additional Files", CSharpSolutionNodeKind.AdditionalFile, tfm.AdditionalFiles);
            AddItemGroup(tfmChildren, "None", CSharpSolutionNodeKind.NoneFile, tfm.NoneFiles);
            children.Add(new CSharpSolutionNode(
                tfm.Name,
                CSharpSolutionNodeKind.TargetFramework,
                project.FullPath,
                tfmChildren,
                string.Equals(tfm.Name, project.SelectedTargetFramework, StringComparison.OrdinalIgnoreCase)));
        }

        return new CSharpSolutionNode(project.Name, CSharpSolutionNodeKind.Project, project.FullPath, children,
            CanRunTests: project.IsTestProject);
    }

    private static void AddItemGroup(
        List<CSharpSolutionNode> destination,
        string groupName,
        CSharpSolutionNodeKind kind,
        IReadOnlyList<ProjectItem> items)
    {
        if (items.Count == 0) return;
        destination.Add(new CSharpSolutionNode(groupName, kind, null,
            items.Select(item => new CSharpSolutionNode(
                    Path.GetFileName(item.FullPath), kind, item.FullPath, []))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    private static void AddFileTree(
        List<CSharpSolutionNode> destination,
        string projectDirectory,
        IReadOnlyList<ProjectItem> items,
        CSharpSolutionNodeKind fileKind)
    {
        var root = new TreeBuilder("", CSharpSolutionNodeKind.Folder, null);
        foreach (var item in items)
        {
            var logical = string.IsNullOrWhiteSpace(item.Link)
                ? Path.GetRelativePath(projectDirectory, item.FullPath)
                : item.Link!;
            var parts = logical.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var cursor = root;
            for (var i = 0; i < parts.Length - 1; i++)
                cursor = cursor.GetOrAdd(parts[i], CSharpSolutionNodeKind.Folder, null);
            cursor.GetOrAdd(parts[^1], fileKind, item.FullPath);
        }
        destination.AddRange(root.Children.Values
            .OrderBy(n => n.Kind == CSharpSolutionNodeKind.Folder ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToNode));
    }

    private sealed class TreeBuilder(string name, CSharpSolutionNodeKind kind, string? fullPath)
    {
        public string Name { get; } = name;
        public CSharpSolutionNodeKind Kind { get; } = kind;
        public string? FullPath { get; } = fullPath;
        public Dictionary<string, TreeBuilder> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public TreeBuilder GetOrAdd(string childName, CSharpSolutionNodeKind childKind, string? childPath)
        {
            if (!Children.TryGetValue(childName, out var child))
                Children[childName] = child = new TreeBuilder(childName, childKind, childPath);
            return child;
        }
    }

    private static CSharpSolutionNode ToNode(TreeBuilder node)
        => new(node.Name, node.Kind, node.FullPath,
            node.Children.Values
                .OrderBy(n => n.Kind == CSharpSolutionNodeKind.Folder ? 0 : 1)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToNode).ToList());
}
