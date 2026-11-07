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

    /// <summary>
    /// 開いていたメニューを閉じたクリックと、続く押下を合わせてダブルクリックと見なすか。
    /// メニュー（StaysOpen=false のポップアップ）の外を押すと、その押下は Win32 側でメニューを閉じるのに
    /// 使われて WPF には届かない。WPF のクリック回数はその押下を数えないので、2回目が単クリック（ClickCount=1）
    /// として届き、ダブルクリックが成立しない——単クリックでメニューが出るこのツリーでは、メニューを
    /// 出したまま別のブランチをダブルクリックすると毎回コミット一覧が切り替わらなかった。
    /// 判定は Windows のダブルクリックと同じ物差し（時間と、縦横それぞれの許容幅＝SM_CXDOUBLECLK の半分）。
    /// </summary>
    internal static bool IsDoubleClickAfterMenuDismiss(
        long elapsedMilliseconds, int dx, int dy,
        uint doubleClickTime, int doubleClickWidth, int doubleClickHeight)
        => elapsedMilliseconds >= 0
           && elapsedMilliseconds <= doubleClickTime
           && Math.Abs(dx) <= doubleClickWidth / 2
           && Math.Abs(dy) <= doubleClickHeight / 2;
}
