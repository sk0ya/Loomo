using System;

namespace sk0ya.Loomo.Services.Search;

/// <summary>
/// ファイル名検索の曖昧一致スコアリング（純ロジック・テスト対象）。
/// スコアは小さいほど一致が強い：前方一致(0) ＞ 部分一致(1) ＞ 飛び石一致(3)。
/// クエリは空白区切りで、全語が一致したものだけを採用する（AND）。
/// パレットの <c>PaletteFilter</c> と同じ思想だが、ファイルパス向けに独立して持つ。
/// </summary>
public static class FuzzyMatcher
{
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
            if (text.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                total += 0;
            else if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                total += 1;
            else if (IsSubsequence(token, text))
                total += 3;
            else
                return null;
        }
        return total;
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
}
