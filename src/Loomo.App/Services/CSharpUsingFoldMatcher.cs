using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>LSP の foldingRange から、C# の using ディレクティブ群に対応する範囲だけを選ぶ。</summary>
internal static class CSharpUsingFoldMatcher
{
    public static IReadOnlyList<LspFoldingRange> Find(string text, IReadOnlyList<LspFoldingRange> ranges)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var usingLines = lines
            .Select((line, index) => (line: line.TrimStart(), index))
            .Where(item => IsUsingDirective(item.line))
            .Select(item => item.index)
            .ToArray();
        if (usingLines.Length < 2)
            return [];

        var groups = new List<(int First, int Last)>();
        var first = usingLines[0];
        var last = first;
        for (var i = 1; i < usingLines.Length; i++)
        {
            if (OnlyTriviaBetween(lines, last + 1, usingLines[i] - 1))
            {
                last = usingLines[i];
                continue;
            }
            if (last > first)
                groups.Add((first, last));
            first = last = usingLines[i];
        }
        if (last > first)
            groups.Add((first, last));

        return groups
            .Select(group => ranges
                // imports 範囲は最初の using 行から始まる。単に包含する外側の namespace/type
                // 範囲を選ぶと using 以外まで閉じるため、開始行の一致を必須にする。
                .Where(range => range.StartLine == group.First && range.EndLine >= group.Last)
                .OrderBy(range => range.EndLine - range.StartLine)
                .FirstOrDefault())
            .Where(range => range is not null)
            .Distinct()
            .ToArray()!;
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
