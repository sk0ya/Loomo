using System.Collections.Generic;

namespace sk0ya.Loomo.App.Layout;

/// <summary>
/// レイアウトモードの保存レイアウト巡回（Ctrl+T）の純ロジック。UI に触れないので単体テストできる
/// （<see cref="PaneLayoutTree"/> と同方針）。巡回位置は -1＝スクラッチ枠、0..count-1＝保存レイアウト。
/// </summary>
public static class LayoutCycleLogic
{
    public readonly record struct Preparation(
        int ActiveIndex,
        bool IsDirty,
        PaneNodeSnapshot? ScratchLayout);

    public readonly record struct CycleTransition(
        int ActiveIndex,
        bool IsDirty,
        PaneNodeSnapshot? ScratchLayout,
        int NextIndex,
        bool ShouldLoad);

    /// <summary>巡回前に現在のツリーを保存済み配置へ寄せるか、スクラッチ枠へ退避するか決める。</summary>
    public static Preparation Prepare(
        IReadOnlyList<SavedLayout> layouts,
        int activeIndex,
        bool isDirty,
        PaneNodeSnapshot? scratchLayout,
        PaneNodeSnapshot? currentLayout)
    {
        if ((!isDirty && activeIndex >= 0) || currentLayout is null)
            return new(activeIndex, isDirty, scratchLayout);

        for (var i = 0; i < layouts.Count; i++)
            if (PaneLayoutTree.SnapshotsEquivalent(layouts[i].Tree, currentLayout))
                return new(i, false, scratchLayout);

        return new(activeIndex, isDirty, currentLayout);
    }

    /// <summary>現在の配置を退避してから巡回先と適用要否をまとめて決める。</summary>
    public static CycleTransition Advance(
        IReadOnlyList<SavedLayout> layouts,
        int activeIndex,
        bool isDirty,
        PaneNodeSnapshot? scratchLayout,
        PaneNodeSnapshot? currentLayout,
        int direction)
    {
        var prepared = Prepare(layouts, activeIndex, isDirty, scratchLayout, currentLayout);
        var next = NextIndex(prepared.ActiveIndex, layouts.Count, prepared.ScratchLayout is not null, direction);
        return new(
            prepared.ActiveIndex,
            prepared.IsDirty,
            prepared.ScratchLayout,
            next,
            next != prepared.ActiveIndex || prepared.IsDirty);
    }

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

    /// <summary>名前付き配置を置換または末尾へ追加し、新しい選択位置を返す。</summary>
    public static int Save(List<SavedLayout> layouts, string name, PaneNodeSnapshot tree)
    {
        var existing = layouts.FindIndex(layout => layout.Name == name);
        var saved = new SavedLayout { Name = name, Tree = tree };
        if (existing >= 0)
        {
            layouts[existing] = saved;
            return existing;
        }
        layouts.Add(saved);
        return layouts.Count - 1;
    }

    /// <summary>配置を削除し、削除に合わせた選択位置を返す。</summary>
    public static bool TryDelete(List<SavedLayout> layouts, int index, int activeIndex, out int nextActiveIndex)
    {
        nextActiveIndex = activeIndex;
        if (index < 0 || index >= layouts.Count)
            return false;
        layouts.RemoveAt(index);
        if (activeIndex == index)
            nextActiveIndex = -1;
        else if (activeIndex > index)
            nextActiveIndex--;
        return true;
    }
}
