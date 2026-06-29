using System;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services.Search;

/// <summary>
/// 検索結果の一括置換で使う純粋な置換ロジック（テスト可能・I/O なし）。grep と同じ規約で
/// リテラル／正規表現・大小区別を解釈し、テキスト全体へ置換を適用して件数を返す。
/// 正規表現の置換文字列は .NET の <c>$1</c> 等の置換構文を尊重する。
/// </summary>
public static class ReplaceEngine
{
    /// <summary>
    /// <paramref name="text"/> 中の <paramref name="query"/> 一致をすべて <paramref name="replacement"/> へ置換する。
    /// 戻り値は置換後テキストと置換件数。一致 0／不正な正規表現／空クエリのときは元のテキストと 0 を返す。
    /// </summary>
    public static (string NewText, int Count) Replace(
        string text, string query, string replacement, bool useRegex, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return (text, 0);

        if (useRegex)
        {
            Regex re;
            try
            {
                re = new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return (text, 0); // 不正な正規表現は何もしない
            }

            var count = re.Matches(text).Count;
            return count == 0 ? (text, 0) : (re.Replace(text, replacement), count);
        }
        else
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var count = CountOccurrences(text, query, comparison);
            return count == 0 ? (text, 0) : (text.Replace(query, replacement, comparison), count);
        }
    }

    /// <summary>重なりのない出現回数を数える（置換と同じ規約：見つかった分だけ前進する）。</summary>
    private static int CountOccurrences(string text, string value, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, comparison)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
