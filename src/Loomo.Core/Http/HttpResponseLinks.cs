using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Http;

/// <summary>
/// .http の応答本文の中の URL（§24.30.1）。ページングの <c>next</c>、作った資源の <c>Location</c>、<c>html_url</c> などを
/// 押して次の一手（ブラウザで開く・同じ .http へ GET を書き足す）にする。純粋関数。
/// </summary>
public static partial class HttpResponseLinks
{
    // JSON の文字列の引用符・山括弧・空白で切る。末尾の句読点は後で剥がす（文の終わりの「.」や括弧書きの「)」）。
    [GeneratedRegex(@"https?://[^\s<>""'`\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', ')', ']', '}', '!', '?'];

    /// <summary>本文の <paramref name="index"/> の文字を含む URL。URL の上でなければ null。</summary>
    public static string? UrlAt(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index > text.Length)
            return null;
        // 1 行の中だけ探す（巨大な本文でも全文を舐めない）。
        var lineStart = index == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[lineStart..lineEnd];
        foreach (Match m in UrlPattern().Matches(line))
        {
            var url = m.Value.TrimEnd(TrailingPunctuation);
            var start = lineStart + m.Index;
            if (index >= start && index < start + url.Length)
                return url;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="after"/> の区画のすぐ後ろへ <c>GET url</c> のリクエストを足す編集。
    /// 区切りの <c>###</c> から書くので、次の区画とも前の区画とも混ざらない。
    /// </summary>
    public static HttpRequestInsertion AppendGet(string documentText, HttpRequestBlock after, string url)
    {
        var lines = documentText.Split('\n');
        var insertLine = after.EndLine + 1;
        if (insertLine < lines.Length)
            return new HttpRequestInsertion(insertLine, 0, $"###\nGET {url}\n\n", insertLine + 1);
        // ファイルの終わり：最後の行の後ろへ。改行で終わっていなければ改行を挟む。
        var lastLine = lines.Length - 1;
        var lastColumn = lines[lastLine].TrimEnd('\r').Length;
        var lead = lines[lastLine].Length == 0 ? "" : "\n";
        // 入れた後の並び：（改行を挟んだら）元の最後の行・空行・### ・GET。
        var requestLine = lastLine + (lead.Length > 0 ? 3 : 2);
        return new HttpRequestInsertion(lastLine, lastColumn, $"{lead}\n###\nGET {url}\n", requestLine);
    }
}

/// <summary>位置（0始まりの行・列）に入れる文字列と、入れた後の <c>GET</c> の行。</summary>
public sealed record HttpRequestInsertion(int Line, int Column, string Text, int RequestLine);
