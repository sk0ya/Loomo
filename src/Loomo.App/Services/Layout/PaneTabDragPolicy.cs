using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ペインタブのドラッグ並べ替えと戻し先の規則。</summary>
internal static class PaneTabDragPolicy
{
    internal static bool ShouldBeginDrag(double horizontalDistance, double verticalDistance,
        double minimumHorizontalDistance, double minimumVerticalDistance)
        => horizontalDistance >= minimumHorizontalDistance || verticalDistance >= minimumVerticalDistance;

    internal static bool ShouldReorder(double horizontalDistance, double verticalDistance,
        double minimumHorizontalDistance, double verticalTolerance)
        => verticalDistance <= verticalTolerance
           && horizontalDistance >= minimumHorizontalDistance;

    internal static bool CanReturnToPane(string paneTag, TabEntryKind returnKind)
        => TabKindForPane(paneTag) == returnKind;

    internal static bool TryMoveToTarget<T>(
        IList<T> items, Func<T, Guid> idOf, Guid draggedId, Guid targetId, out int newIndex)
    {
        newIndex = -1;
        var oldIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (idOf(items[index]) == draggedId)
            {
                oldIndex = index;
                break;
            }
        }
        if (oldIndex < 0)
            return false;

        var targetIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (idOf(items[index]) == targetId)
            {
                targetIndex = index;
                break;
            }
        }
        if (targetIndex < 0 || targetIndex == oldIndex)
            return false;

        var item = items[oldIndex];
        items.RemoveAt(oldIndex);
        items.Insert(targetIndex, item);
        newIndex = targetIndex;
        return true;
    }

    internal static bool TryMoveTabAndEntry<T>(
        IList<T> tabs,
        System.Collections.ObjectModel.ObservableCollection<TabEntryViewModel> entries,
        Func<T, Guid> idOf,
        Guid draggedId,
        Guid targetId)
    {
        if (!TryMoveToTarget(tabs, idOf, draggedId, targetId, out var newIndex))
            return false;
        var oldEntryIndex = IndexOf(entries, draggedId, entry => entry.Id);
        if (oldEntryIndex >= 0 && oldEntryIndex != newIndex)
            entries.Move(oldEntryIndex, newIndex);
        return true;
    }

    internal static int IndexOf<T>(IReadOnlyList<T> items, Guid id, Func<T, Guid> idOf)
    {
        for (var index = 0; index < items.Count; index++)
            if (idOf(items[index]) == id)
                return index;
        return -1;
    }

    private static TabEntryKind? TabKindForPane(string paneTag) => paneTag switch
    {
        "Editor" => TabEntryKind.Editor,
        "Terminal" => TabEntryKind.Terminal,
        "Browser" => TabEntryKind.Browser,
        _ => null,
    };
}
