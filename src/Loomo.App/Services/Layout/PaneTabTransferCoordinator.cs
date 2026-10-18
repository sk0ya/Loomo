namespace sk0ya.Loomo.App.Services;

/// <summary>メインペインからタブを引き出した後、残った分割ビューと選択中タブを整える。</summary>
internal static class PaneTabTransferCoordinator
{
    public static void CompleteRemoval<TTab>(
        IReadOnlyList<TTab> remaining,
        Func<TTab, Guid> idOf,
        PaneSplitView? split,
        int removedIndex,
        bool removedWasActive,
        Action<Guid> activateNeighbor,
        Action<Guid> syncFocusedTab,
        Action createReplacement,
        Func<TTab, DateTime>? lastSeenOf = null)
    {
        if (remaining.Count == 0)
        {
            createReplacement();
            return;
        }

        var validIds = remaining.Select(idOf).ToArray();
        split?.RepairTabs(validIds);
        if (removedWasActive)
        {
            // 隣ではなく直前に見ていたタブへ（§24.44）。最後に見た時刻を持たない呼び出しは隣。
            var next = lastSeenOf is null
                ? Math.Min(removedIndex, remaining.Count - 1)
                : TabCloseSuccessor.Pick(remaining, removedIndex, lastSeenOf);
            activateNeighbor(idOf(remaining[next]));
            return;
        }

        split?.Rebuild();
        if (split?.FocusedTabId is { } focusedId && validIds.Contains(focusedId))
            syncFocusedTab(focusedId);
    }
}
