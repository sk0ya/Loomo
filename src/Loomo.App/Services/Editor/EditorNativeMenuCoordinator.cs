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

    /// <summary>
    /// Diff の左右エディタで外す項目。履歴と全選択は Editor ペインと同じ理由で落とし、さらに
    /// コード操作（移動・名前の変更・修正・説明・整形）も落とす——差分のエディタには LSP を渡さないので
    /// 押しても何も起きず、整形は差分を読んでいる最中に本文を丸ごと動かす。読み取り専用の側（左、
    /// 編集できないときの右）では切り取り・貼り付けも書き換えようがない。
    /// </summary>
    internal static string[] DiffDroppedHeaders(bool readOnly)
    {
        string[] headers =
        [
            .. DroppedHeaders,
            EditorMenuLabels.Navigate,
            EditorMenuLabels.RenameSymbol,
            EditorMenuLabels.CodeActions,
            EditorMenuLabels.FixAllInFile,
            EditorMenuLabels.HoverInfo,
            EditorMenuLabels.FormatDocument,
            EditorMenuLabels.FormatSelection,
        ];
        return readOnly
            ? [.. headers, EditorMenuLabels.CutLine, EditorMenuLabels.CutSelection, EditorMenuLabels.Paste]
            : headers;
    }

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
