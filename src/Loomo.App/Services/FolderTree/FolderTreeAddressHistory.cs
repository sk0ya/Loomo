using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree のアドレス欄で使う、セッション内の入力履歴と候補。</summary>
public sealed class FolderTreeAddressHistory
{
    public const int DefaultCapacity = 20;
    public const int DefaultSuggestionLimit = 12;

    private readonly int _capacity;
    private readonly List<string> _entries = new();

    public FolderTreeAddressHistory(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>新しい順の正規化済み履歴。</summary>
    public IReadOnlyList<string> Entries => _entries;

    public void Add(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return;

        _entries.RemoveAll(existing => PathsEqual(existing, normalized));
        _entries.Insert(0, normalized);
        if (_entries.Count > _capacity)
            _entries.RemoveRange(_capacity, _entries.Count - _capacity);
    }

    /// <summary>
    /// 入力履歴と、入力が区切り文字で終わる場合はそのフォルダー直下のサブフォルダーを返す。
    /// UNC も通常の Windows パスとして扱い、列挙できない共有は履歴候補だけを返す。
    /// </summary>
    public IReadOnlyList<string> Suggest(string? input, string? basePath,
        int limit = DefaultSuggestionLimit)
    {
        if (limit <= 0)
            return Array.Empty<string>();

        var text = input?.Trim() ?? string.Empty;
        var normalizedBase = EndsWithDirectorySeparator(text)
            ? TryNormalizePath(text, basePath, out var typedDirectory) ? typedDirectory : null
            : Normalize(basePath);
        var result = new List<string>(limit);

        AddMatches(_entries, text, result, limit);

        // 「C:\src\」のように区切り文字まで入力したときだけディスクを読む。
        // 入力中の UNC 共有へ毎キー接続しに行かないため、応答性を守る。
        if (EndsWithDirectorySeparator(text)
            && normalizedBase is not null
            && Directory.Exists(normalizedBase))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(normalizedBase)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (result.Count >= limit)
                        break;
                    AddUnique(directory, result);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 候補が出せないだけで、入力・遷移自体は妨げない。
            }
        }

        return result;
    }

    /// <summary>現在地を基準に、絶対パス・相対パス・UNC パスを正規化する。</summary>
    public static bool TryNormalizePath(string? input, string? basePath, out string fullPath)
    {
        fullPath = string.Empty;
        var text = input?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();
        if (text.Length == 0)
            return false;

        return FolderTreeShellNamespaces.TryNormalize(text, basePath, out fullPath);
    }

    private static void AddMatches(IEnumerable<string> paths, string input, List<string> result, int limit)
    {
        foreach (var path in paths)
        {
            if (result.Count >= limit)
                break;
            if (input.Length > 0 && !path.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                continue;
            AddUnique(path, result);
        }
    }

    private static void AddUnique(string path, List<string> result)
    {
        if (!result.Any(existing => PathsEqual(existing, path)))
            result.Add(path);
    }

    private static string? Normalize(string? path)
        => TryNormalizePathWithoutBase(path, out var full) ? full : null;

    private static bool TryNormalizePathWithoutBase(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (FolderTreeShellNamespaces.TryNormalize(path, null, out fullPath))
            return true;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool EndsWithDirectorySeparator(string value)
        => value.EndsWith(Path.DirectorySeparatorChar)
           || value.EndsWith(Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);
}
