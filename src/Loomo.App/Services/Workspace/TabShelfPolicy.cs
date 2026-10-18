namespace sk0ya.Loomo.App.Services;

/// <summary>棚へ下げるかを決める材料（タブ1枚ぶん）。<see cref="IsPinned"/> は人がピン留めしたタブで、
/// 上限の枚数に<b>数えない</b>（ピン留めの枚数に上限は無い）。<see cref="IsProtected"/> は「いま下げてはいけない」
/// ——見ている・未保存・名前が無い・読み込み中など。こちらは数には入るが下げない。</summary>
public readonly record struct TabShelfCandidate(Guid Id, DateTime LastSeenUtc, bool IsProtected, bool IsPinned = false);

/// <summary>
/// タブの棚（§24.29）の判定（純ロジック・テスト対象）。
/// <para>
/// 基準は<b>枚数</b>（JetBrains のタブ上限と同じ）。種類ごと（エディタ／ブラウザ）に上限を超えたら、
/// いちばん長く見ていないタブから棚へ下げる。時間で決める（Arc）と、離れていた間・使っていた量で
/// 結果が揺れて「いつ消えるか」が読めない。枚数なら「11 枚目を開いたら一番古いものが棚へ行く」で言い切れる。
/// ピン留めしたタブは数に入れない（何枚ピン留めしてもよい）。守られているタブ（未保存・見ている…）は
/// 数には入るが下げない。
/// </para>
/// <para>
/// 棚は「自動で閉じたことの取り消し」なので長くは持たない。下げてから <see cref="RetentionDays"/> 日で消える
/// （棚の行をピン留めしたものは残る）。
/// </para>
/// </summary>
public static class TabShelfPolicy
{
    /// <summary>棚に残す上限。古いものから捨てる（ピン留めは捨てない）。</summary>
    public const int Capacity = 100;

    /// <summary>棚に置いておく日数。過ぎたら消える（ピン留めは残る）。</summary>
    public const int RetentionDays = 7;

    /// <summary>既定の枚数上限（種類ごと）。</summary>
    public const int DefaultLimit = 10;

    /// <summary>設定画面で選べる枚数上限。</summary>
    public static readonly IReadOnlyList<int> LimitChoices = [5, 10, 15, 20, 30];

    /// <summary>設定値の枚数上限。範囲外（手で書き換えた settings.json）は既定に戻す。</summary>
    public static int Limit(int value) => value is >= 1 and <= 200 ? value : DefaultLimit;

    /// <summary>
    /// 上限を超えた分として棚へ下げるタブ。ピン留めは数えずに、残りが上限を超えた分だけ、いちばん長く見ていないもの
    /// （同じなら並びの前＝先に開いたもの）から守られていないものを選ぶ。守られたものだけで上限を超えているなら、
    /// それ以上は下げない。
    /// </summary>
    public static IReadOnlyList<Guid> SelectOverflow(IReadOnlyList<TabShelfCandidate> tabs, int limit)
    {
        var excess = tabs.Count(t => !t.IsPinned) - limit;
        if (excess <= 0)
            return [];
        return tabs
            .Select((tab, index) => (tab, index))
            .Where(t => !t.tab.IsPinned && !t.tab.IsProtected)
            .OrderBy(t => t.tab.LastSeenUtc)
            .ThenBy(t => t.index)
            .Take(excess)
            .Select(t => t.tab.Id)
            .ToList();
    }

    /// <summary>期限切れの棚の行（ピン留めしていない・下げてから <see cref="RetentionDays"/> 日以上）。</summary>
    public static IReadOnlyList<Guid> Expired(IEnumerable<ShelvedTabSnapshot> shelf, DateTime nowUtc)
        => shelf.Where(s => !s.Pinned && nowUtc - s.ShelvedUtc >= TimeSpan.FromDays(RetentionDays))
            .Select(s => s.Id)
            .ToList();

    /// <summary>あと何日で消えるか（ピン留めは null）。</summary>
    public static int? DaysLeft(ShelvedTabSnapshot item, DateTime nowUtc)
        => item.Pinned ? null : Math.Max(0, (int)Math.Ceiling(RetentionDays - (nowUtc - item.ShelvedUtc).TotalDays));

    /// <summary>
    /// 棚へ足す。新しい順に先頭へ積み、同じもの（同じファイル・同じ URL）の古い項目は外す
    /// （同じファイルを何度も下げても棚に1行。古い行のピン留めは引き継ぐ）。<see cref="Capacity"/> を超えたら
    /// ピン留めしていない古い分から捨てる。
    /// </summary>
    public static List<ShelvedTabSnapshot> Merge(
        IReadOnlyList<ShelvedTabSnapshot> shelf, IReadOnlyList<ShelvedTabSnapshot> added)
    {
        var result = new List<ShelvedTabSnapshot>(added.Count + shelf.Count);
        foreach (var item in added.Concat(shelf))
        {
            if (result.FirstOrDefault(existing => IsSameTarget(existing, item)) is { } kept)
            {
                kept.Pinned |= item.Pinned;
                continue;
            }
            result.Add(item);
        }
        for (var i = result.Count - 1; i >= 0 && result.Count > Capacity; i--)
        {
            if (!result[i].Pinned)
                result.RemoveAt(i);
        }
        return result;
    }

    /// <summary>同じものを指す項目か（エディタはパス、ブラウザは URL）。</summary>
    public static bool IsSameTarget(ShelvedTabSnapshot a, ShelvedTabSnapshot b)
    {
        if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal))
            return false;
        return a.Kind == ShelvedTabSnapshot.EditorKind
            ? a.FilePath is { Length: > 0 } && FilePathRelations.AreEqual(a.FilePath, b.FilePath)
            : a.Url is { Length: > 0 } && string.Equals(a.Url, b.Url, StringComparison.Ordinal);
    }
}
