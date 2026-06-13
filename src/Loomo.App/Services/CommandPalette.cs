using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// コマンドパレット（部屋全体の操作統一）の1コマンド。カテゴリ＋名前で検索され、
/// 選択されると <see cref="Execute"/> が走る。一覧の構築は ShellWindow が担う。
/// </summary>
public sealed record PaletteCommand(string Category, string Title, Action Execute, string? Shortcut = null)
{
    /// <summary>検索対象のテキスト（カテゴリも含めて引っかける）。</summary>
    public string SearchText => $"{Category} {Title}";
}

/// <summary>パレットの絞り込み（純ロジック・テスト対象）。</summary>
public static class PaletteFilter
{
    /// <summary>
    /// 空白区切りの各語がすべて一致するコマンドを、一致の強さ
    /// （名前の前方一致 ＞ 部分一致 ＞ 文字の飛び石一致）で並べて返す。
    /// 空クエリは元の順のまま全件。
    /// </summary>
    public static IReadOnlyList<PaletteCommand> Filter(IReadOnlyList<PaletteCommand> commands, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return commands;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<(PaletteCommand Command, int Score, int Index)>();

        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            var total = 0;
            var matched = true;
            foreach (var token in tokens)
            {
                if (command.Title.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    total += 0;
                else if (command.SearchText.Contains(token, StringComparison.OrdinalIgnoreCase))
                    total += 1;
                else if (IsSubsequence(token, command.SearchText))
                    total += 3;
                else
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                scored.Add((command, total, i));
        }

        return scored
            .OrderBy(s => s.Score)
            .ThenBy(s => s.Index)
            .Select(s => s.Command)
            .ToList();
    }

    /// <summary>needle の文字が haystack に順番どおり現れるか（大文字小文字無視の飛び石一致）。</summary>
    internal static bool IsSubsequence(string needle, string haystack)
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
