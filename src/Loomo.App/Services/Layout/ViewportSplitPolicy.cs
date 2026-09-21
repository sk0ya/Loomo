using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Services;

/// <summary>分割ビューポートのキー操作と、分割元ファイルの解決規則。</summary>
internal static class ViewportSplitPolicy
{
    public static int? NextTabIndex(int currentIndex, int count, int step)
    {
        if (count <= 1)
            return null;
        if (currentIndex < 0)
            currentIndex = 0;
        return ((currentIndex + step) % count + count) % count;
    }

    public static string ResolveTerminalDirectory(string? sourceDirectory, string fallbackDirectory)
        => !string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory)
            ? sourceDirectory
            : fallbackDirectory;

    public static bool EditorPathMatches(string? editorPath, string? path)
    {
        if (editorPath is not { Length: > 0 } || string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool CanCloseFocused(PaneKind? pane, int editorLeafCount, int terminalLeafCount)
        => pane switch
        {
            PaneKind.Editor => editorLeafCount > 1,
            PaneKind.Terminal => terminalLeafCount > 1,
            _ => false,
        };

    public static ViewportSplitAction? ResolveKey(PaneKind? pane, ViewportSplitKey key)
    {
        if (pane is not { } focusedPane || focusedPane is not (PaneKind.Editor or PaneKind.Terminal))
            return null;

        var orientation = key switch
        {
            ViewportSplitKey.Vertical => SplitKind.Columns,
            ViewportSplitKey.Horizontal => SplitKind.Rows,
            _ => (SplitKind?)null,
        };
        return new(focusedPane, orientation);
    }

    /// <summary>分割元または新規タブ要求の相対パスを、既存ファイルへ解決する。</summary>
    public static string? ResolveEditorPath(
        string? filePath,
        string? sourceFilePath,
        string? workspaceRoot,
        string? terminalDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        if (Path.IsPathRooted(filePath))
            return File.Exists(filePath) ? Path.GetFullPath(filePath) : null;

        var sourceDirectory = string.IsNullOrWhiteSpace(sourceFilePath)
            ? null
            : Path.GetDirectoryName(sourceFilePath);
        foreach (var directory in new[] { sourceDirectory, workspaceRoot, terminalDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            var candidate = Path.GetFullPath(Path.Combine(directory, filePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>分割先へ既存ファイルを読み込むか、未保存本文を複製するかを決める。</summary>
    public static ViewportEditorSplitContent ResolveEditorSplitContent(
        string? requestedPath,
        string? sourceFilePath,
        bool sourceIsModified,
        string? sourceText)
    {
        if (requestedPath is { Length: > 0 })
            return new(requestedPath, null);
        if (!string.IsNullOrWhiteSpace(sourceFilePath) && File.Exists(sourceFilePath) && !sourceIsModified)
            return new(sourceFilePath, null);
        return new(null, sourceText);
    }
}

internal sealed record ViewportEditorSplitContent(string? FilePath, string? Text);

internal enum ViewportSplitKey
{
    Close,
    Vertical,
    Horizontal,
}

internal sealed record ViewportSplitAction(PaneKind Pane, SplitKind? Orientation);
