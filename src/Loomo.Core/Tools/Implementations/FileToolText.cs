using System;

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

    /// <summary>承認カード用に文字列を 1 行・短く要約する（改行は ⏎、長文は省略）。</summary>
    public static string Preview(string text, int max = 40)
    {
        var oneLine = text.Replace("\r\n", "⏎").Replace('\n', '⏎').Replace('\r', '⏎');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
