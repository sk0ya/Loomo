using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Services;

internal sealed record CSharpSolutionMenuAction(CSharpSolutionAction Action, string Header);

/// <summary>C# Solution Explorer の対象解決と、ノード種別ごとの操作メニュー構成。</summary>
internal static class CSharpSolutionExplorerPolicy
{
    internal static string? SelectedTargetFrameworkFor(SolutionModel? solution, string? targetPath)
    {
        if (solution is null || string.IsNullOrWhiteSpace(targetPath)) return null;
        try { return solution.ProjectForTarget(targetPath)?.SelectedTargetFramework; }
        catch (ArgumentException) { return null; }
    }

    internal static IReadOnlyList<IReadOnlyList<CSharpSolutionMenuAction>> SolutionActionGroups(
        CSharpSolutionNodeKind kind, bool canRunTests)
    {
        var primary = new List<CSharpSolutionMenuAction>
        {
            new(CSharpSolutionAction.Build, "ビルド"),
        };
        if (canRunTests)
        {
            primary.Add(new(CSharpSolutionAction.Test, "テスト"));
            primary.Add(new(CSharpSolutionAction.DebugTests, "テストをデバッグ"));
        }

        var fix = kind == CSharpSolutionNodeKind.Project
            ? new CSharpSolutionMenuAction(CSharpSolutionAction.FixAllProject, "Fix All（プロジェクト）")
            : new CSharpSolutionMenuAction(CSharpSolutionAction.FixAllSolution, "Fix All（ソリューション）");
        var groups = new List<IReadOnlyList<CSharpSolutionMenuAction>> { primary, new[] { fix } };
        if (kind == CSharpSolutionNodeKind.Project)
            groups.Add(new CSharpSolutionMenuAction[]
            {
                new(CSharpSolutionAction.Run, "実行"),
                new(CSharpSolutionAction.Debug, "デバッグ"),
            });
        return groups;
    }

    /// <summary>選択ノードを含むプロジェクトを返す。ソリューション外なら null。</summary>
    internal static CSharpSolutionNodeViewModel? FindOwningProject(CSharpSolutionNodeViewModel? node)
    {
        for (; node is not null; node = node.Parent)
            if (node.Kind == CSharpSolutionNodeKind.Project)
                return node;
        return null;
    }

    internal static TreeViewItem? ExpansionTargetOnMouseUp(int clickCount, DependencyObject? source)
    {
        if (clickCount != 1 || source is null
            || WpfTreeTraversal.FindAncestor<ToggleButton>(source) is not null)
            return null;
        return WpfTreeTraversal.FindAncestor<TreeViewItem>(source) is
            { DataContext: CSharpSolutionNodeViewModel { Children.Count: > 0 } } item
            ? item
            : null;
    }

    internal static bool ShouldSuppressDoubleClickExpansion(int clickCount, DependencyObject? source)
        => clickCount == 2
            && source is not null
            && WpfTreeTraversal.FindAncestor<TreeViewItem>(source) is
                { DataContext: CSharpSolutionNodeViewModel { Children.Count: > 0 } };

    /// <summary>BringIntoView の対象を上下のどちらへ何ピクセル動かすかを返す。</summary>
    internal static double VerticalScrollCorrection(double top, double bottom, double viewportHeight)
        => top < 0 ? top : bottom > viewportHeight ? bottom - viewportHeight : 0;
}
