namespace sk0ya.Loomo.App.Services;

/// <summary>番号でタブを選ぶ（§24.47）。ピン留め（§24.29）を先頭に、残りは並びのまま数える。
/// 9 は「最後のタブ」——ブラウザと同じで、タブが 9 枚を超えても末尾へ 1 打で行ける。</summary>
internal static class TabNumberPolicy
{
    /// <summary>番号（1〜9）に当たるタブの位置。無ければ -1。</summary>
    public static int Pick<TTab>(IReadOnlyList<TTab> tabs, int number, Func<TTab, bool> isPinned)
    {
        if (tabs.Count == 0 || number is < 1 or > 9)
            return -1;
        var ordered = Enumerable.Range(0, tabs.Count)
            .OrderBy(i => isPinned(tabs[i]) ? 0 : 1)   // OrderBy は安定なので、同じ組の中は元の並び
            .ToList();
        if (number == 9)
            return ordered[^1];
        return number <= ordered.Count ? ordered[number - 1] : -1;
    }
}
