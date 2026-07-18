using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペースセッションの復元判断と表示モデル変換。</summary>
public static class WorkspaceSessionCoordinator
{
    public static bool ResolveSoloMode(WorkspaceSnapshot workspace) => workspace.Mode switch
    {
        DisplayMode.Solo => true,
        DisplayMode.Layout => false,
        _ => workspace.Stage?.IsActive == true,
    };

    public static string NormalizeBrowserAddress(string? text, string defaultUrl)
    {
        var address = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
            return defaultUrl;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
            return uri.ToString();
        if (address.Contains(' '))
            return $"https://www.google.com/search?q={Uri.EscapeDataString(address)}";
        var scheme = address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                     || address.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            ? "http://"
            : "https://";
        return scheme + address;
    }

    internal static void RestoreEditor(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
        {
            editor.LoadFile(snapshot.FilePath);
            if (!snapshot.IsModified)
            {
                RestoreEditorViewState(editor, snapshot);
                return;
            }
        }
        if (snapshot.IsModified || string.IsNullOrWhiteSpace(snapshot.FilePath))
        {
            editor.SetText(snapshot.LoadText());
            RestoreEditorViewState(editor, snapshot);
            return;
        }
        editor.SetText(string.Empty);
    }

    internal static EditorTabSnapshot CaptureEditorTab(EditorTab tab, Guid? activeTabId)
    {
        var isActive = tab.Id == activeTabId;
        if (!tab.IsRealized && tab.Pending is { } pending)
        {
            return new EditorTabSnapshot
            {
                Id = tab.Id,
                FilePath = pending.FilePath,
                Text = pending.Text,
                DeferredTextPath = pending.DeferredTextPath,
                Title = pending.Title,
                IsModified = pending.IsModified,
                IsActive = isActive,
                CaretLine = pending.CaretLine,
                CaretColumn = pending.CaretColumn,
                ScrollRatio = pending.ScrollRatio
            };
        }

        var editor = tab.Control;
        return new EditorTabSnapshot
        {
            Id = tab.Id,
            FilePath = editor.FilePath,
            Text = editor.Text,
            Title = string.IsNullOrWhiteSpace(editor.FilePath) ? "Untitled" : Path.GetFileName(editor.FilePath),
            IsModified = editor.IsModified,
            IsActive = isActive,
            CaretLine = editor.Caret.Line,
            CaretColumn = editor.Caret.Column,
            ScrollRatio = editor.VerticalScrollRatio
        };
    }

    private static void RestoreEditorViewState(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (snapshot.CaretLine > 0 || snapshot.CaretColumn > 0)
            editor.NavigateTo(snapshot.CaretLine, snapshot.CaretColumn);
        if (snapshot.ScrollRatio is { } ratio and > 0)
            editor.Dispatcher.BeginInvoke(
                new Action(() => editor.ScrollToVerticalRatio(ratio)), DispatcherPriority.Loaded);
    }
}
