using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Markdown のタブへファイルを落としたときに入れるリンクの文字（§24.110 の 3）。落とした先の文書のフォルダーからの相対パスで、
/// 画像なら <c>![名前](パス)</c>、それ以外は <c>[名前](パス)</c>。複数なら 1 行に 1 つ。
/// </summary>
internal static class MarkdownDropLinks
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

    /// <summary>落とし先がリンクを受ける Markdown か（保存先の無い無題のタブは相対パスを作れないので、いままでどおり開く）。</summary>
    public static bool IsMarkdownDocument(string? documentPath)
        => !string.IsNullOrEmpty(documentPath)
           && Path.GetExtension(documentPath).ToLowerInvariant() is ".md" or ".markdown" or ".mdx" or ".mdown";

    /// <param name="sourceLine">ターミナルのリンク（<c>src/foo.cs:12</c>）から運んできた行（§24.52）。1 つだけ運んだときに <c>#L12</c> を付ける。</param>
    public static string Build(string documentPath, IReadOnlyList<string> paths, int sourceLine = 0)
    {
        var baseFolder = Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? "";
        var lines = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            var target = LinkPath(baseFolder, full);
            if (ImageExtensions.Contains(Path.GetExtension(full)))
            {
                lines.Add($"![{EscapeText(Path.GetFileNameWithoutExtension(full))}]({target})");
                continue;
            }
            var fragment = paths.Count == 1 && sourceLine > 0 ? $"#L{sourceLine}" : "";
            var label = fragment.Length > 0 ? $"{name}:{sourceLine}" : name;
            lines.Add($"[{EscapeText(label)}]({target}{fragment})");
        }
        return string.Join("\n", lines);
    }

    /// <summary>同じドライブなら相対（区切りは /）。別ドライブは絶対パス（/ 区切り）。空白は %20 にする——
    /// 日本語などはそのまま残す（本文で読めるように。Loomo のリンク解決は %20 を戻す）。</summary>
    internal static string LinkPath(string baseFolder, string fullPath)
    {
        var sameRoot = string.Equals(Path.GetPathRoot(baseFolder), Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
        var path = sameRoot ? Path.GetRelativePath(baseFolder, fullPath) : fullPath;
        path = path.Replace('\\', '/').Replace(" ", "%20");
        // 括弧は釣り合っていればリンクの宛先として読めるが、片方だけだと宛先がそこで切れる。
        if (path.Count(c => c == '(') != path.Count(c => c == ')'))
            path = path.Replace("(", "%28").Replace(")", "%29");
        return path;
    }

    private static string EscapeText(string text) => text.Replace("[", "\\[").Replace("]", "\\]");
}
