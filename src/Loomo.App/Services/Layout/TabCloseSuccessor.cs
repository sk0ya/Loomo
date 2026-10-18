namespace sk0ya.Loomo.App.Services;

/// <summary>前に出ていたタブを閉じたあと、どのタブを出すか（§24.44）。隣ではなく<b>直前に見ていたタブ</b>——
/// 定義へ飛んで読んで閉じたら、飛ぶ前のファイルに戻る。閉じる操作が「戻る」になる。</summary>
internal static class TabCloseSuccessor
{
    /// <summary>残ったタブから次に出す位置を選ぶ。最後に見た時刻がいちばん新しいもの。時刻を1つも持っていない
    /// （復元しただけで、どれもまだ見ていない）ときは従来どおり閉じた位置の隣。同じ時刻なら閉じた位置に近いもの。</summary>
    public static int Pick<TTab>(IReadOnlyList<TTab> remaining, int removedIndex, Func<TTab, DateTime> lastSeenOf)
    {
        if (remaining.Count == 0)
            return -1;
        var neighbor = Math.Min(Math.Max(removedIndex, 0), remaining.Count - 1);
        var best = -1;
        var bestSeen = default(DateTime);
        for (var i = 0; i < remaining.Count; i++)
        {
            var seen = lastSeenOf(remaining[i]);
            if (seen == default)
                continue;
            if (best < 0 || seen > bestSeen
                || (seen == bestSeen && Math.Abs(i - neighbor) < Math.Abs(best - neighbor)))
            {
                best = i;
                bestSeen = seen;
            }
        }
        return best < 0 ? neighbor : best;
    }
}
