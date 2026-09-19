using System;
using System.Collections.Generic;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.CSharp;

/// <summary>C# の using ディレクティブ群がどの行からどの行までかを、<b>本文だけ</b>から決める。
///
/// <para>以前は LSP の foldingRange から「using 節に当たる範囲」を選んでいた。だが実測で Roslyn は
/// using 節を範囲として返さないことがあり、そのうえ答えが届くのは開いてから 0.5 秒後——本文が
/// 読めるようになってから畳まれて文字が動く。using の固まりは文字を見れば分かるので、
/// サーバーに頼るのをやめた。</para></summary>
public static class CSharpUsingFoldMatcher
{
    /// <summary>畳める using の固まり。1 行しかない固まりは畳んでも意味がないので返さない。</summary>
    public static IReadOnlyList<LspFoldingRange> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var usingLines = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (IsUsingDirective(lines[i].TrimStart()))
                usingLines.Add(i);
        if (usingLines.Count < 2) return [];

        var groups = new List<LspFoldingRange>();
        var first = usingLines[0];
        var last = first;
        for (var i = 1; i < usingLines.Count; i++)
        {
            // 空行やコメントを挟んだだけなら同じ固まり（using をグループ分けして書く流儀がある）。
            if (OnlyTriviaBetween(lines, last + 1, usingLines[i] - 1))
            {
                last = usingLines[i];
                continue;
            }
            if (last > first) groups.Add(new LspFoldingRange(first, last));
            first = last = usingLines[i];
        }
        if (last > first) groups.Add(new LspFoldingRange(first, last));

        return groups;
    }

    private static bool IsUsingDirective(string line)
    {
        if (line.StartsWith("global using ", StringComparison.Ordinal))
            return true;
        if (!line.StartsWith("using ", StringComparison.Ordinal))
            return false;
        // using (...) / using var ... はディレクティブではない。
        var rest = line["using ".Length..].TrimStart();
        return !rest.StartsWith('(') && !rest.StartsWith("var ", StringComparison.Ordinal);
    }

    private static bool OnlyTriviaBetween(string[] lines, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
