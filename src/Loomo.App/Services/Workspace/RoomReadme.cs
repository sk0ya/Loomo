namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 初めて開く部屋で、最初に目に入れておく README（§24.98・Zed 1.27 の <c>on_new_window: "readme"</c>）。
/// 復元するタブが無い部屋だけが対象で、戻ってきた部屋は復元が優先。
/// </summary>
internal static class RoomReadme
{
    /// <summary>探す順。Markdown を先に（EditorSupport のプレビューで読める）。</summary>
    private static readonly string[] Candidates =
        ["README.md", "README.markdown", "readme.md", "Readme.md", "README.txt", "README.rst", "README"];

    /// <summary>フォルダーの直下の README のフルパス。無ければ null。</summary>
    public static string? Find(string folder) => Find(folder, Directory.Exists, Directory.EnumerateFiles);

    internal static string? Find(string folder, Func<string, bool> folderExists,
        Func<string, IEnumerable<string>> enumerateFiles)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !folderExists(folder)) return null;
            // 大文字小文字の違いは Windows では同じファイルだが、列挙した実名で返す（タブの見出しに出る名前）。
            var names = enumerateFiles(folder)
                .Select(path => (Path: path, Name: Path.GetFileName(path)))
                .ToList();
            foreach (var candidate in Candidates)
                if (names.FirstOrDefault(n => string.Equals(n.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    is { Path: { } path })
                    return path;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
