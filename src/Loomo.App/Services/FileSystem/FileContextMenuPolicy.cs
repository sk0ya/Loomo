namespace sk0ya.Loomo.App.Services;

/// <summary>選択内容や操作可否からファイル系コンテキストメニューの表示を決める。</summary>
internal readonly record struct FileContextMenuState(
    int SelectionCount,
    int FileCount,
    bool SingleIsDirectory,
    bool SingleIsHtml,
    bool CanSearchInSingleDirectory,
    bool CanPin,
    bool IsPinned,
    bool QuickAccessReady,
    bool CanPinToQuickAccess,
    bool CanUnpinFromQuickAccess,
    bool CanGit,
    bool CanAddToGitignore,
    bool CanRunAi)
{
    public bool HasSingleSelection => SelectionCount == 1;
}

internal readonly record struct FileHistoryMenuPresentation(string Header, bool IsVisible);

internal static class FileContextMenuPolicy
{
    public static bool IsVisible(string? tag, FileContextMenuState state, bool historyVisible)
        => tag switch
        {
            "Selection" => state.SelectionCount > 0,
            "Single" => state.HasSingleSelection,
            "FileOnly" => state.HasSingleSelection && !state.SingleIsDirectory,
            "DirOnly" => state.HasSingleSelection && state.SingleIsDirectory,
            "Html" => state.HasSingleSelection && state.SingleIsHtml,
            "CompareTwo" => state.FileCount == 2,
            "SearchableDir" => state.HasSingleSelection && state.SingleIsDirectory
                && state.CanSearchInSingleDirectory,
            "Pinnable" => state.CanPin,
            "Unpinnable" => state.IsPinned,
            "QuickAccessPinnable" => state.QuickAccessReady && state.CanPinToQuickAccess,
            "QuickAccessUnpinnable" => state.QuickAccessReady && state.CanUnpinFromQuickAccess,
            "GitMenu" => state.CanGit,
            "GitBlame" => state.HasSingleSelection && !state.SingleIsDirectory && state.CanGit,
            "GitIgnore" => state.CanAddToGitignore,
            "AiMenu" => state.SelectionCount > 0 && state.CanRunAi,
            // Undo/Redo は選択ではなく履歴で決まる。
            "UndoItem" or "RedoItem" => historyVisible,
            _ => true,
        };

    public static bool ShouldShowAiMenu(bool isAiReady, int selectionCount, bool hasNonShellItem)
        => isAiReady && selectionCount > 0 && hasNonShellItem;

    public static bool HasExactlyTwoFiles(int fileCount) => fileCount == 2;

    public static FileAiAction? ResolveFileAiAction(string? tag)
        => tag switch
        {
            "FileAiSummarize" => FileAiAction.Summarize,
            "FileAiReview" => FileAiAction.Review,
            "FileAiGenerateTests" => FileAiAction.GenerateTests,
            "FileAiFindRelated" => FileAiAction.FindRelated,
            _ => null,
        };

    public static FileHistoryMenuPresentation FormatHistoryAction(string verb, string? description)
        => new(
            description is null ? verb : $"{verb}（{description}）",
            description is not null);

    public static string FormatDeleteConfirmation(
        int selectionCount, bool singleIsDirectory, string? singleName)
        => selectionCount == 1
            ? $"{(singleIsDirectory ? "フォルダー" : "ファイル")}「{singleName}」をゴミ箱へ移動しますか？"
            : $"選択した {selectionCount} 件をゴミ箱へ移動しますか？";
}
