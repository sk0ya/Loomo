namespace sk0ya.Loomo.App.Services;

/// <summary>タブをキーボードで並べ替える（§24.77）。ピン留めの組と外れた組はまたがない——
/// 番号（§24.47）はピン留めを先頭に数えるので、組をまたいで動かすと番号の意味が崩れる。</summary>
internal static class TabMovePolicy
{
    /// <summary><paramref name="index"/> のタブを <paramref name="delta"/>（-1＝左、+1＝右）へ動かすときに
    /// 入れ替える相手の位置。同じ組に隣が無ければ -1。</summary>
    public static int Target<TTab>(IReadOnlyList<TTab> tabs, int index, int delta, Func<TTab, bool> isPinned)
    {
        if (index < 0 || index >= tabs.Count || delta == 0)
            return -1;
        var step = Math.Sign(delta);
        var pinned = isPinned(tabs[index]);
        for (var i = index + step; i >= 0 && i < tabs.Count; i += step)
        {
            if (isPinned(tabs[i]) == pinned)
                return i;
        }

        return -1;
    }

    /// <summary><paramref name="from"/> の要素を <paramref name="to"/> の位置へ動かす（間の要素は 1 つずつずれる）。</summary>
    public static void Move<TTab>(IList<TTab> tabs, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= tabs.Count || to >= tabs.Count)
            return;
        var item = tabs[from];
        tabs.RemoveAt(from);
        tabs.Insert(to, item);
    }
}
