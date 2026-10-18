using System.Net;
using sk0ya.Loomo.Core.Markdown;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザのタブから写す URL とリンク（§24.46）。タブを前に出さずに、調べ物をエディタやコンポーザへ運ぶ。</summary>
internal static class BrowserTabLink
{
    /// <summary>写す URL。ローカルのページ（<c>https://file.loomo/…</c>）は Loomo の外では開けないので <c>file:///</c> に戻す。
    /// 写せるものが無ければ null。</summary>
    public static string? Url(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (LocalFileUrl.TryToPath(url, out var path))
            return new Uri(path).AbsoluteUri + uri.Query + uri.Fragment;
        return uri.Scheme is "http" or "https" or "file" ? url : null;
    }

    /// <summary>Markdown のリンク（<c>[題](URL)</c>）。題が空なら URL をそのまま題にする。</summary>
    public static string? Markdown(string? title, string? url)
    {
        if (Url(url) is not { } link)
            return null;
        var text = string.IsNullOrWhiteSpace(title) ? link : EscapeText(title.Trim());
        return $"[{text}]({MarkdownLinkParser.EncodeDestination(link)})";
    }

    /// <summary>リッチテキストへ貼ったときの &lt;a&gt;。</summary>
    public static string? Html(string? title, string? url)
    {
        if (Url(url) is not { } link)
            return null;
        var text = string.IsNullOrWhiteSpace(title) ? link : title.Trim();
        return $"<a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(text)}</a>";
    }

    /// <summary>題の中の角括弧とバックスラッシュはリンクの区切りと読まれるので逃がす。改行は空白に。</summary>
    private static string EscapeText(string title)
        => title.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("\r", " ").Replace("\n", " ");
}
