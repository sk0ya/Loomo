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
        Action createReplacement)
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
            activateNeighbor(idOf(remaining[Math.Min(removedIndex, remaining.Count - 1)]));
            return;
        }

        split?.Rebuild();
        if (split?.FocusedTabId is { } focusedId && validIds.Contains(focusedId))
            syncFocusedTab(focusedId);
    }
}
