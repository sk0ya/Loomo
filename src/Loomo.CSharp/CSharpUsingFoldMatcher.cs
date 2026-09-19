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
        // ブロックコメントの中身は本文として読まない。読むと、コメントアウトされた using の固まりを
        // 畳んでしまう——しかもこの折りたたみは手動扱い（IsManual）で作られるので、サーバーからの
        // foldingRange 更新では二度と外れず、頼んでいない折りたたみが居座る。
        lines = StripBlockComments(lines);
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

    /// <summary>
    /// 各行から <c>/* … */</c> の中身を空白で消した写しを返す（行数と桁位置は変えない＝範囲の行番号が
    /// ずれない）。コメントが消えた行は空行・<c>//</c> と同じ「区切りではないもの」として扱われるので、
    /// using の固まりの間にブロックコメントを挟んでも固まりは切れない。
    ///
    /// <para>文字列リテラルの中の <c>/*</c> は見ていない。using の固まりはファイルの頭に居て、そこに
    /// 文字列は出てこないうえ、外した結果も<b>畳み方が変わるだけ</b>（本文の解釈には使わない）なので、
    /// ここで字句解析まで持ち込まない。</para>
    /// </summary>
    private static string[] StripBlockComments(string[] lines)
    {
        var stripped = new string[lines.Length];
        var inComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var builder = new System.Text.StringBuilder(line.Length);
            for (var c = 0; c < line.Length; c++)
            {
                if (inComment)
                {
                    if (c + 1 < line.Length && line[c] == '*' && line[c + 1] == '/')
                    {
                        inComment = false;
                        builder.Append("  ");
                        c++;
                        continue;
                    }
                    builder.Append(' ');
                    continue;
                }
                // 行コメントより後ろは、そこに /* があってもブロックの開始ではない。
                if (c + 1 < line.Length && line[c] == '/' && line[c + 1] == '/')
                {
                    builder.Append(line[c..]);
                    break;
                }
                if (c + 1 < line.Length && line[c] == '/' && line[c + 1] == '*')
                {
                    inComment = true;
                    builder.Append("  ");
                    c++;
                    continue;
                }
                builder.Append(line[c]);
            }
            stripped[i] = builder.ToString();
        }
        return stripped;
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
