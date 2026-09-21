namespace sk0ya.Loomo.App.Services;

/// <summary>フォルダー選択 UI が表示する物理ディレクトリを列挙する。</summary>
public static class FileSystemDirectoryQuery
{
    public static IReadOnlyList<string> EnumerateDirectories(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public static bool HasDirectories(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
