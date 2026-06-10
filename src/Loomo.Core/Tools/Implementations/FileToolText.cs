using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Tools.Implementations;

/// <summary>write_file / edit_file が共有する小さなテキスト補助（行数・出現数・置換・要約表示）。</summary>
internal static class FileToolText
{
    /// <summary>行数（<see cref="Services"/> の差分表示と同じ規約：空文字は 0 行）。</summary>
    public static int CountLines(string text)
        => text.Length == 0 ? 0 : text.Split('\n').Length;

    /// <summary><paramref name="text"/> 中に <paramref name="needle"/> が現れる回数（重なり無し・序数比較）。</summary>
    public static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>最初に現れた <paramref name="needle"/> 1 箇所だけを <paramref name="replacement"/> に置換する。</summary>
    public static string ReplaceFirst(string text, string needle, string replacement)
    {
        var i = text.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return text;
        return string.Concat(text.AsSpan(0, i), replacement, text.AsSpan(i + needle.Length));
    }

    /// <summary>
    /// <paramref name="needle"/> が完全一致しなかったとき、大文字小文字・改行コード（CRLF/LF）の違いだけで
    /// 一致する箇所を探し、ファイル上の<b>実際のテキスト</b>を返す（無ければ null）。
    /// edit_file の not-found エラーに「実物をそのままコピーすれば通る」ヒントとして添えるための補助。
    /// 小モデルは old_string の大小文字を推測で書いて外し、エラー後に虚偽の完了報告へ流れる
    /// （例: 実体 "Version:" に対し "version:" を指定）ため、復旧を機械的な複写作業に変える。
    /// 置換の自動適用はしない（大小文字が意味を持つ編集を誤らせないため、ヒント提示のみ）。
    /// </summary>
    public static string? FindNearMatch(string text, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return null;
        foreach (var candidate in NearMatchCandidates(needle))
        {
            var i = text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return text.Substring(i, candidate.Length);
        }
        return null;
    }

    /// <summary>近傍候補：改行コード違いに加え、JSON の二重エスケープをモデルがそのまま渡した形
    /// （<c>\"</c> や <c>\\</c> が文字として残った old_string）も剥がして探す。</summary>
    private static IEnumerable<string> NearMatchCandidates(string needle)
    {
        foreach (var v in NewlineVariants(needle)) yield return v;
        if (!needle.Contains("\\\"") && !needle.Contains("\\\\")) yield break;
        var unescaped = needle.Replace("\\\\", "\\").Replace("\\\"", "\"");
        foreach (var v in NewlineVariants(unescaped)) yield return v;
    }

    /// <summary>改行コード違い（そのまま／CRLF化／LF化）の探索候補。</summary>
    private static IEnumerable<string> NewlineVariants(string needle)
    {
        yield return needle;
        if (!needle.Contains('\n')) yield break;
        var lf = needle.Replace("\r\n", "\n");
        var crlf = lf.Replace("\n", "\r\n");
        if (!string.Equals(crlf, needle, StringComparison.Ordinal)) yield return crlf;
        if (!string.Equals(lf, needle, StringComparison.Ordinal)) yield return lf;
    }

    /// <summary><paramref name="needle"/> を含む行を（重複を除き）最大 <paramref name="max"/> 行返す。
    /// edit_file の複数一致エラーで「この行全体を old_string にコピーすれば一意になる」具体例を示すための補助。
    /// 長い行は要約する（エラー本文の肥大を防ぐ）。</summary>
    public static List<string> LinesContaining(string text, string needle, int max)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.Contains(needle, StringComparison.Ordinal)) continue;
            if (result.Contains(line)) continue;
            result.Add(line.Length <= 120 ? line : line[..120] + "…");
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>承認カード用に文字列を 1 行・短く要約する（改行は ⏎、長文は省略）。</summary>
    public static string Preview(string text, int max = 40)
    {
        var oneLine = text.Replace("\r\n", "⏎").Replace('\n', '⏎').Replace('\r', '⏎');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
