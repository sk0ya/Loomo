namespace sk0ya.Loomo.App.Services;

/// <summary>エディタ標準メニューの項目をホスト側の操作へ差し替える。</summary>
internal static class EditorNativeMenuCoordinator
{
    internal static readonly string[] DroppedHeaders =
    [
        EditorMenuLabels.Undo,
        EditorMenuLabels.Redo,
        EditorMenuLabels.SelectAll,
    ];

    internal static void Adjust(
        ContextMenu menu,
        bool hasEditor,
        Func<MenuItem> buildQuickFix,
        Func<MenuItem> buildHover)
    {
        RemoveByHeader(menu, DroppedHeaders);
        if (!hasEditor)
            return;
        ReplaceByHeader(menu, EditorMenuLabels.CodeActions, buildQuickFix);
        ReplaceByHeader(menu, EditorMenuLabels.HoverInfo, buildHover);
    }

    internal static void RemoveByHeader(ContextMenu menu, IReadOnlyList<string> headers)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
            if (menu.Items[i] is MenuItem item && HasHeader(item, headers))
                menu.Items.RemoveAt(i);
    }

    internal static bool ReplaceByHeader(
        ContextMenu menu, string header, Func<MenuItem> replacement)
    {
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i] is not MenuItem item || !HasHeader(item, [header]))
                continue;
            menu.Items[i] = replacement();
            return true;
        }
        return false;
    }

    private static bool HasHeader(MenuItem item, IReadOnlyList<string> headers)
        => item.Header is string text &&
           headers.Any(header => string.Equals(header, text, StringComparison.Ordinal));
}
