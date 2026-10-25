using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>部屋のタブ 1 枚（種類を問わない・§24.65）。</summary>
internal readonly record struct RoomTabRef(TabEntryKind Kind, Guid Id);

/// <summary>
/// エディタ・ターミナル・ブラウザのタブを、種類を問わず最近使った順に覚える（Ctrl+Tab の巡回・§24.65・Zed 1.25.2 のタブスイッチャー／
/// Tabby v1.0.236 の MRU タブ切り替え）。「使った」はタブを選んだときだけでなく、そのタブを映しているペインへ焦点が入ったときも数える——
/// 各タブの <c>LastSeenUtc</c>（棚・閉じたときの戻り先）はタブを選んだときしか動かないので、エディタで書いてからずっと前に選んだ
/// ターミナルへ戻った、が並びに出ない。純ロジック。
/// </summary>
internal sealed class RoomTabMru
{
    /// <summary>覚えておく上限。閉じたタブはここから消さず、並べるときに生きているタブだけを拾う。</summary>
    private const int Capacity = 256;

    private readonly List<RoomTabRef> _order = [];

    public void Touch(RoomTabRef tab)
    {
        if (_order.Count > 0 && _order[0] == tab)
            return;
        _order.Remove(tab);
        _order.Insert(0, tab);
        if (_order.Count > Capacity)
            _order.RemoveAt(_order.Count - 1);
    }

    /// <summary>生きているタブを最近使った順に。まだ一度も使っていないタブ（復元した直後など）は、その後ろへ最後に見た時刻の新しい順。</summary>
    public IReadOnlyList<T> Order<T>(IEnumerable<T> live, Func<T, RoomTabRef> key, Func<T, DateTime> lastSeenUtc)
    {
        var rank = new Dictionary<RoomTabRef, int>();
        for (var i = 0; i < _order.Count; i++)
            rank.TryAdd(_order[i], i);
        return live
            .OrderBy(t => rank.TryGetValue(key(t), out var i) ? i : int.MaxValue)
            .ThenByDescending(lastSeenUtc)
            .ToList();
    }
}
