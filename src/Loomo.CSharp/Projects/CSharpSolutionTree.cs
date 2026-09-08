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
    bool CanRunTests = false)
{
    /// <summary>名前の後ろへ淡色で添える補助情報（TFM名・件数など）。行を増やさずに
    /// 「単一TFMなら畳む」「グループの件数を出す」を賄うための1列。</summary>
    public string? Detail { get; init; }

    /// <summary><see cref="Detail"/> が子の件数から作られているときの単位（<c>""</c>＝数字だけ、
    /// <c>" プロジェクト"</c> など）。null＝件数ではない（"テスト" などの札）。絞り込みで枝を
    /// 間引いたら数字を作り直すのに使う——そのまま持ち越すと「その他ファイル 42」の下に1件、
    /// のような嘘の数字が残る。</summary>
    public string? DetailCountUnit { get; init; }

    /// <summary>子の件数から <see cref="Detail"/> を作った同じ node を返す（単位つき）。</summary>
    public CSharpSolutionNode WithChildCountDetail(string unit)
        => this with { Detail = $"{Children.Count}{unit}", DetailCountUnit = unit };
}

/// <summary>評価済みの <see cref="SolutionModel"/> を solution／project／TFM／folder／file の
/// 表示階層へ変換する。表示側はMSBuild XMLや相対パスを再解釈しない。</summary>
public static class CSharpSolutionTreeBuilder
{
    public static CSharpSolutionNode Build(SolutionModel solution)
        => new(solution.Name, CSharpSolutionNodeKind.Solution, solution.FullPath,
            solution.Projects.Select(BuildProject).ToList(),
            CanRunTests: solution.Projects.Any(project => project.IsTestProject))
        {
            Detail = solution.Projects.Count > 0 ? $"{solution.Projects.Count} プロジェクト" : null,
            DetailCountUnit = solution.Projects.Count > 0 ? " プロジェクト" : null,
        };

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
                    .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList())
            {
                Detail = projectReferences.Count.ToString(),
                DetailCountUnit = "",
            });
        }

        // TFMが1つだけのプロジェクト（大多数）は、その段を挟まずファイルを直接ぶら下げる。
        // 挟むと「プロジェクトを開く→net10.0を開く」の二度手間になるうえ、選択肢が1つしか
        // 無いので選ぶ意味がない。単一TFMの名前は行にも出さない——狭い左列ではプロジェクト名を
        // 削ってまで見せる価値がなく、選ぶ余地がある多TFMのときだけ段として現れれば足りる。
        var single = project.TargetFrameworks.Count == 1 ? project.TargetFrameworks[0] : null;
        if (single is not null)
        {
            AddTargetFrameworkChildren(children, project, single);
        }
        else
        {
            foreach (var tfm in project.TargetFrameworks)
            {
                var tfmChildren = new List<CSharpSolutionNode>();
                AddTargetFrameworkChildren(tfmChildren, project, tfm);
                children.Add(new CSharpSolutionNode(
                    tfm.Name,
                    CSharpSolutionNodeKind.TargetFramework,
                    project.FullPath,
                    tfmChildren,
                    string.Equals(tfm.Name, project.SelectedTargetFramework, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return new CSharpSolutionNode(project.Name, CSharpSolutionNodeKind.Project, project.FullPath, children,
            CanRunTests: project.IsTestProject)
        {
            // 添え字はテスト印だけ。テストプロジェクトを一覧から拾えることには行の幅を割く価値がある。
            Detail = project.IsTestProject ? "テスト" : null,
        };
    }

    private static void AddTargetFrameworkChildren(
        List<CSharpSolutionNode> destination, ProjectModel project, TargetFrameworkModel tfm)
    {
        AddFileTree(destination, project.Directory, tfm.CompileFiles, CSharpSolutionNodeKind.File);
        AddItemGroup(destination, "アナライザー", CSharpSolutionNodeKind.Analyzer, tfm.Analyzers);
        AddItemGroup(destination, "追加ファイル", CSharpSolutionNodeKind.AdditionalFile, tfm.AdditionalFiles);
        AddItemGroup(destination, "その他ファイル", CSharpSolutionNodeKind.NoneFile, tfm.NoneFiles);
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
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            Detail = items.Count.ToString(),
            DetailCountUnit = "",
        });
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

/// <summary>絞り込み結果。<c>Root</c> が null なら一致なし。</summary>
public sealed record CSharpSolutionFilterResult(
    CSharpSolutionNode? Root, int Matched, bool Truncated);

/// <summary>名前による絞り込み。ツリーは1万ノード級になり得るので、一致した枝だけを残した
/// 新しいツリーを作って返す。表示側は結果を全部開く前提なので、件数に上限を設ける
/// （上限なしに「.cs」で絞ると全ファイルを展開してしまい、畳んである意味が消える）。
/// 空白区切りは AND。純関数なのでテストから直接叩ける。</summary>
public static class CSharpSolutionTreeFilter
{
    public const int DefaultLimit = 300;

    public static CSharpSolutionFilterResult Apply(
        CSharpSolutionNode root, string? query, int limit = DefaultLimit)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return new CSharpSolutionFilterResult(root, 0, false);

        var matched = 0;
        var truncated = false;
        var kept = Prune(root, isRoot: true);
        return new CSharpSolutionFilterResult(kept, matched, truncated);

        CSharpSolutionNode? Prune(CSharpSolutionNode node, bool isRoot)
        {
            // ルート（ソリューション）自身は器なので一致に数えない。数えると
            // ソリューション名に当たった瞬間に全部が「一致」になり、絞り込みが効かない。
            var self = !isRoot && terms.All(term =>
                node.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (self)
            {
                if (matched >= limit)
                {
                    truncated = true;
                    return null;
                }
                matched++;
            }

            var children = new List<CSharpSolutionNode>();
            foreach (var child in node.Children)
                if (Prune(child, isRoot: false) is { } keptChild) children.Add(keptChild);

            if (!self && children.Count == 0) return null;
            var kept = node with { Children = children };
            // 件数の Detail は間引いた後の数で作り直す。持ち越すと「その他ファイル 42」の下に
            // 1件、のような嘘になる（表示は絞り込み結果なのに数字だけ全体を指してしまう）。
            return node.DetailCountUnit is { } unit && children.Count != node.Children.Count
                ? kept.WithChildCountDetail(unit)
                : kept;
        }
    }
}
