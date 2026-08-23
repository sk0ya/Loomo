using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree の標準キーボード検索で使う、UI に依存しない選択ヘルパー。</summary>
internal static class FolderTreeKeyboardNavigation
{
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
}
