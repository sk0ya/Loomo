using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>アプリ内／Explorer間のファイルドラッグで共有する、UI非依存のパス処理。</summary>
public static class FileDragDrop
{
    public static void SetPaths(DataObject data, IEnumerable<string> paths)
    {
        var normalized = ExistingPaths(paths);
        if (normalized.Count == 0) return;
        var files = new StringCollection();
        files.AddRange(normalized.ToArray());
        data.SetFileDropList(files);
    }

    public static IReadOnlyList<string> TryGetPaths(IDataObject? data)
    {
        try
        {
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
                return Array.Empty<string>();
            var raw = data.GetData(DataFormats.FileDrop);
            return raw switch
            {
                string[] array => ExistingPaths(array),
                StringCollection collection => ExistingPaths(collection.Cast<string>()),
                IEnumerable enumerable => ExistingPaths(enumerable.OfType<string>()),
                _ => Array.Empty<string>()
            };
        }
        catch
        {
            // Explorer can revoke or lazily materialize its IDataObject while a drag is
            // being cancelled. Such data is simply an invalid drop, never a UI error.
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> ExistingPaths(IEnumerable<string>? paths)
    {
        if (paths is null) return Array.Empty<string>();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { continue; }
            if ((!File.Exists(full) && !Directory.Exists(full)) || !seen.Add(full)) continue;
            result.Add(full);
        }
        return result;
    }

    public static string PowerShellQuote(string path)
        => "'" + (path ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>複数項目を検索／Gitの1つの範囲へまとめる。共通親が無ければ null。</summary>
    public static string? CommonDirectory(IEnumerable<string>? paths)
    {
        var normalized = ExistingPaths(paths);
        if (normalized.Count == 0) return null;
        var directories = normalized.Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path))
            .Where(path => !string.IsNullOrEmpty(path)).Cast<string>().ToArray();
        if (directories.Length == 0) return null;
        var candidate = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            if (!string.Equals(Path.GetPathRoot(candidate), Path.GetPathRoot(directory),
                    StringComparison.OrdinalIgnoreCase))
                return null;
            while (!IsSameOrDescendant(directory, candidate))
            {
                var parent = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
                    return null;
                candidate = parent;
            }
        }
        return candidate;
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        var left = TrimDirectory(path);
        var right = TrimDirectory(parent);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
