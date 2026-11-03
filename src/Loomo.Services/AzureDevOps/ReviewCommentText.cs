using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>コメントの1切れ：文字か、画像（<paramref name="ImageUrl"/> があれば画像で、<paramref name="Text"/> はその代替の文字）。</summary>
public sealed record ReviewCommentPart(string Text, string? ImageUrl = null);

/// <summary>
/// レビューのコメントの本文（Markdown）を、文字と画像に切り分ける（純粋）。Azure DevOps に貼った画像は
/// <c>![image.png](https://dev.azure.com/…/attachments/…)</c> になる。HTML の <c>&lt;img src="…"&gt;</c> も拾う。
/// </summary>
public static partial class ReviewCommentText
{
    [GeneratedRegex("""!\[(?<alt>[^\]]*)\]\((?<url>https?://[^)\s]+)\)|<img\b[^>]*?\bsrc\s*=\s*["'](?<src>https?://[^"']+)["'][^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePattern();

    public static IReadOnlyList<ReviewCommentPart> Split(string content)
    {
        var parts = new List<ReviewCommentPart>();
        var last = 0;
        foreach (Match match in ImagePattern().Matches(content))
        {
            if (match.Index > last)
                parts.Add(new ReviewCommentPart(content[last..match.Index]));
            var url = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["src"].Value;
            parts.Add(new ReviewCommentPart(match.Groups["alt"].Value, url));
            last = match.Index + match.Length;
        }
        if (last < content.Length)
            parts.Add(new ReviewCommentPart(content[last..]));
        return parts;
    }
}
