using System.Threading.Tasks;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタのナビゲーション、プレビュー配置、外部ファイル変更の検出を行う。</summary>
internal static class EditorTabNavigationPolicy
{
    public static string? NormalizeExistingFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        return Path.GetFullPath(path);
    }

    public static EditorTab? FindOpenFileTab(IReadOnlyList<EditorTab> tabs, string path)
        => tabs.FirstOrDefault(tab =>
            string.Equals(tab.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>フォルダーまたはファイルの名前変更後に、開いているファイルの新しいパスを返す。</summary>
    public static string? PathAfterRename(string? filePath, string oldPath, string newPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        if (isDirectory && FilePathRelations.IsAncestorOf(oldPath, filePath))
            return Path.GetFullPath(Path.Combine(newPath, Path.GetRelativePath(oldPath, filePath)));
        if (!isDirectory && FilePathRelations.AreEqual(filePath, oldPath))
            return newPath;
        return null;
    }

    /// <summary>削除されたファイルまたはフォルダー配下にあるタブ ID を返す。</summary>
    public static IReadOnlyList<Guid> TabsAffectedByDeletion(IEnumerable<EditorTab> tabs, string deletedPath)
        => tabs.Where(tab => tab.PeekFilePath is { Length: > 0 } path
                && FilePathRelations.IsSameOrAncestorOf(deletedPath, path))
            .Select(tab => tab.Id)
            .ToList();

    /// <summary>遅延実体化を起こさず、ナビゲーション対象のアクティブファイルを得る。</summary>
    public static string? ActiveFilePath(EditorTab? tab)
    {
        var path = tab is null ? null : tab.IsRealized ? tab.Control.FilePath : tab.PeekFilePath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    public static EditorTab? ResolvePreviewReuseTarget(
        IReadOnlyList<EditorTab> tabs, EditorTab? preview, EditorTab? active)
    {
        if (preview is not null && tabs.Contains(preview)
            && !preview.PeekIsModified && !preview.PeekIsVirtual)
            return preview;

        if (active is not null && tabs.Contains(active)
            && string.IsNullOrEmpty(active.PeekFilePath) && !active.PeekIsModified
            && !active.PeekIsVirtual && active.VirtualTitle is null)
            return active;

        return null;
    }

    public static void MovePreviewTabToEnd(List<EditorTab> tabs, EditorTab preview)
    {
        var index = tabs.FindIndex(tab => ReferenceEquals(tab, preview));
        var last = tabs.Count - 1;
        if (index < 0 || index == last)
            return;
        tabs.RemoveAt(index);
        tabs.Add(preview);
    }

    public static async Task<string?> FindChangedExternalFileAsync(EditorTab tab)
    {
        var editor = tab.Control;
        var path = editor.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || editor.IsModified)
            return null;

        string diskText;
        try { diskText = await File.ReadAllTextAsync(path); }
        catch { return null; }
        return EolInsensitiveText.Equals(diskText, editor.Text) ? null : path;
    }
}
