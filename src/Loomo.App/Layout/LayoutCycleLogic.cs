using System.Collections.Generic;

namespace sk0ya.Loomo.App.Layout;

/// <summary>
/// レイアウトモードの保存レイアウト巡回（Ctrl+T）の純ロジック。UI に触れないので単体テストできる
/// （<see cref="PaneLayoutTree"/> と同方針）。巡回位置は -1＝スクラッチ枠、0..count-1＝保存レイアウト。
/// </summary>
public static class LayoutCycleLogic
{
    /// <summary>
    /// 現在位置 <paramref name="current"/> から次の巡回位置を返す。巡回列は
    /// スクラッチ有り＝[-1, 0, 1, … count-1]、無し＝[0, 1, … count-1]。前後にラップする。
    /// 現在位置が列に無い（例：削除済み）場合は端から入る。
    /// </summary>
    public static int NextIndex(int current, int count, bool hasScratch, int direction)
    {
        var positions = new List<int>();
        if (hasScratch)
            positions.Add(-1);
        for (var i = 0; i < count; i++)
            positions.Add(i);

        if (positions.Count == 0)
            return current;

        var idx = positions.IndexOf(current);
        if (idx < 0)
            return direction >= 0 ? positions[0] : positions[^1];

        var step = direction >= 0 ? 1 : -1;
        var next = ((idx + step) % positions.Count + positions.Count) % positions.Count;
        return positions[next];
    }
}
