using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>ページに並べる画像1枚：<paramref name="Url"/> は置き場の URL、<paramref name="FilePath"/> があれば
/// 下にファイル名のリンク（押せばエディタで開く）、<paramref name="Caption"/> は添え書き（無ければファイル名）。</summary>
internal sealed record TerminalSupportImage(string Url, string? FilePath = null, string? Caption = null);

/// <summary>
/// ターミナルの中身に出てきた画像を、横の面で絵として見せ直す（§39.10.2・§39.10）。ターミナルは画像ファイルの名前を
/// 文字として流すだけ（スクリーンショットを撮るスクリプト、画像を書き出すビルド、<c>ls</c>）——同じ中身を、名前ではなく
/// 絵で見せる。ここは純粋な部分：出力の文字から画像ファイルの名前を拾うのと、縮小画像の並びの HTML。
/// </summary>
internal static class TerminalOutputImages
{
    /// <summary>1回の出力から拾う上限。<c>ls</c> が何百枚並べても、縮小画像の壁にはしない。</summary>
    public const int MaxImages = 12;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    public static bool IsImageExtension(string extension)
        => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    // 引用符で囲んだもの（空白を含みうる）と、空白で区切られた1語。拡張子で終わるものだけ。
    private static readonly Regex Quoted = new(
        @"[""'](?<p>[^""'\r\n<>|*?]+?\.(?:png|jpe?g|gif|bmp|webp))[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Bare = new(
        @"(?<![\w.\\/:])(?<p>(?:[A-Za-z]:[\\/]|\\\\|~[\\/]|\.{1,2}[\\/]|[\\/])?[\w\-.+@\\/]*?[\w\-+@]\.(?:png|jpe?g|gif|bmp|webp))(?![\w\\/])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 出力の文字から、実在する画像ファイルのフルパスを現れた順に（重複なし・最大 <see cref="MaxImages"/>）。
    /// 相対パスは <paramref name="workingDirectory"/>（コマンドを実行した場所）から解く。<paramref name="exists"/> で
    /// 確かめたものだけを返す——名前らしく見えるだけの文字列（<c>logo.png が見つかりません</c> の後ろのほうなど）で
    /// 壊れた絵を並べない。
    /// </summary>
    public static IReadOnlyList<string> FindPaths(string text, string? workingDirectory, Func<string, bool> exists)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text))
            return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = Quoted.Matches(text).Select(m => (m.Index, Value: m.Groups["p"].Value))
            .Concat(Bare.Matches(text).Select(m => (m.Index, Value: m.Groups["p"].Value)))
            .OrderBy(c => c.Index);
        foreach (var (_, value) in candidates)
        {
            if (Resolve(value.Trim(), workingDirectory) is not { } full || !seen.Add(full))
                continue;
            bool ok;
            try { ok = exists(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
            if (!ok)
                continue;
            found.Add(full);
            if (found.Count >= MaxImages)
                break;
        }
        return found;
    }

    private static string? Resolve(string path, string? workingDirectory)
    {
        try
        {
            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            // Git Bash の /c/... は C:\...
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == '/')
                path = char.ToUpperInvariant(path[1]) + ":" + path[2..];
            if (Path.IsPathFullyQualified(path))
                return Path.GetFullPath(path);
            if (string.IsNullOrEmpty(workingDirectory) || path.StartsWith('/') || path.StartsWith('\\'))
                return null;
            return Path.GetFullPath(Path.Combine(workingDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>縮小画像の並び。押すと大きく（もう一度押すと戻る）。<paramref name="title"/> は並びの見出し（無ければ出さない）。
    /// <paramref name="withStyle"/> が false なら体裁（<see cref="Style"/>）を付けない——並びを何度も出すページは自分で1回入れる。</summary>
    public static string Strip(IReadOnlyList<TerminalSupportImage> images, string? title = null, bool withStyle = true)
    {
        if (images.Count == 0)
            return "";
        var html = new StringBuilder();
        if (withStyle)
            html.Append(Style);
        html.Append("<div class=\"tsi\">");
        if (title is { Length: > 0 })
            html.Append("<div class=\"tsi-title\">").Append(MarkdownRenderer.Encode(title)).Append("</div>");
        html.Append("<div class=\"tsi-row\">");
        foreach (var image in images)
        {
            html.Append("<figure class=\"tsi-item\">");
            html.Append("<img src=\"").Append(MarkdownRenderer.EncodeAttribute(image.Url))
                .Append("\" loading=\"lazy\" alt=\"\" title=\"押すと拡大\" onclick=\"this.parentNode.classList.toggle('tsi-zoom')\">");
            var caption = image.Caption ?? (image.FilePath is { } p ? Path.GetFileName(p) : null);
            if (caption is { Length: > 0 })
            {
                html.Append("<figcaption>");
                if (image.FilePath is { } file)
                    html.Append("<a href=\"").Append(MarkdownRenderer.EncodeAttribute(new Uri(file).AbsoluteUri))
                        .Append("\" title=\"").Append(MarkdownRenderer.EncodeAttribute(file)).Append("\">")
                        .Append(MarkdownRenderer.Encode(caption)).Append("</a>");
                else
                    html.Append(MarkdownRenderer.Encode(caption));
                html.Append("</figcaption>");
            }
            html.Append("</figure>");
        }
        html.Append("</div></div>");
        return html.ToString();
    }

    /// <summary>並びの体裁。色はテーマの文字色からの透過で作る（会話の体裁と同じ方針）。</summary>
    public const string Style = """
        <style>
        .tsi { margin: .4em 0 .9em; }
        .tsi-title { font-size: .78em; opacity: .6; margin-bottom: .3em; }
        .tsi-row { display: flex; flex-wrap: wrap; gap: .5em; align-items: flex-start; }
        .tsi-item { margin: 0; max-width: 100%; }
        .tsi-item img { display: block; max-height: 140px; max-width: 240px; cursor: zoom-in; border-radius: 3px;
                        border: 1px solid color-mix(in srgb, currentColor 18%, transparent); background:
                        repeating-conic-gradient(color-mix(in srgb, currentColor 8%, transparent) 0 25%, transparent 0 50%) 0 0 / 12px 12px; }
        .tsi-item.tsi-zoom { flex-basis: 100%; }
        .tsi-item.tsi-zoom img { max-height: none; max-width: 100%; cursor: zoom-out; }
        .tsi-item figcaption { font-size: .75em; opacity: .7; margin-top: .15em; max-width: 240px;
                               overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .tsi-item.tsi-zoom figcaption { max-width: none; }
        .tsi-item figcaption a { text-decoration: none; }
        </style>
        """;
}
