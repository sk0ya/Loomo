using System.IO;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル操作で使うパスの関係を判定する。</summary>
public static class FilePathRelations
{
    /// <summary>パスの末尾要素を表示名にし、ルートなど要素がないときはパス全体を返す。</summary>
    internal static string FileNameOrPath(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>末尾区切りと相対表記を正規化して、2つのパスが同じ場所か判定する。</summary>
    public static bool AreEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(
                WorkspacePaths.Normalize(left), WorkspacePaths.Normalize(right),
                StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> が <paramref name="directory"/> の子孫か判定する。</summary>
    public static bool IsAncestorOf(string? directory, string? path)
        => WorkspacePaths.IsWithin(directory, path) && !AreEqual(directory, path);

    /// <summary><paramref name="path"/> が <paramref name="directory"/> 自身か子孫かを判定する。</summary>
    public static bool IsSameOrAncestorOf(string? directory, string? path)
        => AreEqual(directory, path) || IsAncestorOf(directory, path);

    /// <summary>同じ階層から、対象パスそのものか次に展開すべき親フォルダーを探す。</summary>
    internal static (T? Target, T? Descend) FindPathMatch<T>(
        IEnumerable<T> siblings,
        string fullPath,
        Func<T, string> getPath,
        Func<T, bool> isDirectory)
        where T : class
    {
        foreach (var item in siblings)
        {
            var itemPath = getPath(item);
            if (AreEqual(itemPath, fullPath))
                return (item, null);
            if (isDirectory(item) && IsAncestorOf(itemPath, fullPath))
                return (null, item);
        }

        return (null, null);
    }
}
