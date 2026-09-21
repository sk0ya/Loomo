namespace sk0ya.Loomo.App.Services;

internal enum GitBranchTreeClickAction
{
    Ignore,
    CancelPendingMenu,
    ToggleFolder,
    SelectBranch,
}

/// <summary>ブランチツリーのクリックを、行の種類とクリック位置に応じた操作へ変換する。</summary>
internal static class GitBranchTreeClickPolicy
{
    internal static GitBranchTreeClickAction Resolve(
        int clickCount,
        bool onExpansionToggle,
        bool hasTreeNode,
        bool isFolder)
    {
        if (clickCount > 1)
            return GitBranchTreeClickAction.CancelPendingMenu;
        if (onExpansionToggle || !hasTreeNode)
            return GitBranchTreeClickAction.Ignore;
        return isFolder
            ? GitBranchTreeClickAction.ToggleFolder
            : GitBranchTreeClickAction.SelectBranch;
    }
}
