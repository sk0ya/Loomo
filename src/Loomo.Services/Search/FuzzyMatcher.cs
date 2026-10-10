using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services.Search;

/// <summary>
/// ファイル名検索の曖昧一致スコアリング（純ロジック・テスト対象）。
/// スコアは小さいほど一致が強い：
/// 前方一致(0) ＞ 先頭の語からの CamelHumps 一致(1) ＞ 部分一致(2) ＞ 途中の語からの CamelHumps 一致(3) ＞ 飛び石一致(4)。
/// クエリは空白区切りで、全語が一致したものだけを採用する（AND）。
/// <para>CamelHumps は Rider の Go to File／Symbol と同じ考え方で、語の頭（大文字・区切り文字の直後・数字の頭）を
/// 拾っていく一致。<c>ELH</c>・<c>EdLoHi</c> が <c>EditorLocationHistory</c> に当たる。ただの飛び石一致だと
/// 「どこかに E・L・H がこの順で出てくる」ファイルすべてと同点になり、狙ったファイルが埋もれていた。</para>
/// パレットの <c>PaletteFilter</c> も同じ尺度を使う。
/// </summary>
public static class FuzzyMatcher
{
    public const int PrefixScore = 0;
    public const int LeadingHumpScore = 1;
    public const int ContainsScore = 2;
    public const int HumpScore = 3;
    public const int SubsequenceScore = 4;

    /// <summary><paramref name="text"/> が <paramref name="query"/> に一致するならスコア、しなければ null。
    /// 空クエリは常に 0（全件一致）。</summary>
    public static int? Score(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0;
        if (string.IsNullOrEmpty(text))
            return null;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var total = 0;
        foreach (var token in tokens)
        {
            if (TokenScore(text, token) is not { } score)
                return null;
            total += score;
        }
        return total;
    }

    /// <summary>1語ぶんのスコア（一致しなければ null）。</summary>
    public static int? TokenScore(string text, string token)
    {
        if (text.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return PrefixScore;
        var hump = MatchHumps(text, token);
        if (hump == HumpMatch.Leading)
            return LeadingHumpScore;
        if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            return ContainsScore;
        if (hump == HumpMatch.Inner)
            return HumpScore;
        if (IsSubsequence(token, text))
            return SubsequenceScore;
        return null;
    }

    /// <summary>needle の文字が haystack に順番どおり現れるか（大文字小文字無視の飛び石一致）。</summary>
    public static bool IsSubsequence(string needle, string haystack)
    {
        var n = 0;
        foreach (var c in haystack)
        {
            if (n < needle.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(needle[n]))
                n++;
        }
        return n == needle.Length;
    }

    public enum HumpMatch { None, Leading, Inner }

    /// <summary>
    /// <paramref name="token"/> を語の頭の並びとして読めるか。各語からは頭の1文字以上を続けて取り、
    /// 語は飛ばしてよい（<c>ELH</c>／<c>EdLoHi</c>／<c>LocHis</c> → <c>EditorLocationHistory</c>）。
    /// 先頭の語から当たれば <see cref="HumpMatch.Leading"/>。1文字のクエリは対象外（何にでも当たるだけ）。
    /// </summary>
    public static HumpMatch MatchHumps(string text, string token)
    {
        if (token.Length < 2 || string.IsNullOrEmpty(text))
            return HumpMatch.None;
        var starts = HumpStarts(text);
        // 失敗した (語, クエリ位置) を覚えて、パスのように語が多い対象でも組合せ爆発させない。
        var failed = new HashSet<(int, int)>();
        for (var first = 0; first < starts.Count; first++)
        {
            if (MatchFrom(text, starts, first, token, 0, failed))
                return first == 0 ? HumpMatch.Leading : HumpMatch.Inner;
        }
        return HumpMatch.None;
    }

    /// <summary>語 <paramref name="hump"/> の頭からクエリの <paramref name="t"/> 文字目以降を読めるか。</summary>
    private static bool MatchFrom(
        string text, List<int> starts, int hump, string token, int t, HashSet<(int, int)> failed)
    {
        if (failed.Contains((hump, t)))
            return false;
        var start = starts[hump];
        var end = hump + 1 < starts.Count ? starts[hump + 1] : text.Length;
        var max = 0;
        while (start + max < end && t + max < token.Length &&
               char.ToUpperInvariant(text[start + max]) == char.ToUpperInvariant(token[t + max]))
            max++;
        // この語から k 文字（1 以上）取り、残りを後ろの語で読む。長く取る方から試す。
        for (var k = max; k >= 1; k--)
        {
            if (t + k == token.Length)
                return true;
            for (var next = hump + 1; next < starts.Count; next++)
            {
                if (MatchFrom(text, starts, next, token, t + k, failed))
                    return true;
            }
        }
        failed.Add((hump, t));
        return false;
    }

    /// <summary>語の頭の位置：先頭・小文字/数字の後の大文字・英数字以外の後の英数字・英字の後の数字。
    /// <c>HTMLParser</c> のような大文字の連なりは、次の小文字の直前の大文字も頭とみなす（<c>P</c>）。</summary>
    private static List<int> HumpStarts(string text)
    {
        var starts = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
                continue;
            if (i == 0)
            {
                starts.Add(i);
                continue;
            }
            var prev = text[i - 1];
            var isStart =
                !char.IsLetterOrDigit(prev)
                || (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev)))
                || (char.IsDigit(c) && char.IsLetter(prev))
                || (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < text.Length && char.IsLower(text[i + 1]));
            if (isStart)
                starts.Add(i);
        }
        return starts;
    }
}
