using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree の標準キーボード検索で使う、UI に依存しない選択ヘルパー。</summary>
internal static class FolderTreeKeyboardNavigation
{
    /// <summary>展開中の枝だけを表示順（深さ優先）で列挙する。</summary>
    public static IEnumerable<FileNodeViewModel> EnumerateVisibleNodes(
        IEnumerable<FileNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.IsDirectory && node.IsExpanded)
                foreach (var child in EnumerateVisibleNodes(node.Children))
                    yield return child;
        }
    }

    /// <summary>
    /// 表示中ノード列における相対移動先を返す。端ではそこで止まり、未選択時は
    /// 最初のノードを現在地として扱う（FolderTree の初期フォーカスと同じ）。
    /// </summary>
    public static int FindAdjacentIndex(int count, int currentIndex, int delta)
    {
        if (count <= 0)
            return -1;

        if (currentIndex < 0 || currentIndex >= count)
            return 0;

        var current = currentIndex;
        return Math.Clamp(current + delta, 0, count - 1);
    }

    /// <summary>
    /// 入力文字列で始まる項目を、現在位置の次から循環して探す。
    /// 入力が2文字以上の継続入力なら現在位置自身も候補に含めるため、
    /// 1文字目で選ばれた項目をそのまま絞り込める。
    /// </summary>
    public static int FindTypeAheadMatch(
        IReadOnlyList<string> names,
        string input,
        int currentIndex)
    {
        if (names.Count == 0 || string.IsNullOrEmpty(input))
            return -1;

        var start = currentIndex < 0 ? 0 : currentIndex;
        if (input.Length == 1)
            start++;
        start %= names.Count;

        for (var offset = 0; offset < names.Count; offset++)
        {
            var index = (start + offset) % names.Count;
            if (names[index].StartsWith(input, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <summary>連続入力で名前を探し、累積入力で不一致なら直近の文字だけで再検索する。</summary>
    public static (string Input, int MatchIndex) ResolveTypeAheadSearch(
        IReadOnlyList<FileNodeViewModel> nodes,
        string previousInput,
        string latestInput,
        int currentIndex)
        => ResolveTypeAheadSearch(nodes, node => node.Name, previousInput, latestInput, currentIndex);

    /// <summary>連続入力と直近入力の両方を試し、項目名から検索位置を求める。</summary>
    public static (string Input, int MatchIndex) ResolveTypeAheadSearch<T>(
        IReadOnlyList<T> items,
        Func<T, string> getName,
        string previousInput,
        string latestInput,
        int currentIndex)
    {
        var names = items.Select(getName).ToArray();
        var input = previousInput + latestInput;
        var matchIndex = FindTypeAheadMatch(names, input, currentIndex);
        if (matchIndex < 0 && input.Length > latestInput.Length)
        {
            input = latestInput;
            matchIndex = FindTypeAheadMatch(names, input, currentIndex);
        }

        return (input, matchIndex);
    }
}
