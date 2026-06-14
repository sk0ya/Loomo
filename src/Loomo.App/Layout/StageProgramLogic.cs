using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Layout;

/// <summary>配置モードで舞台に立つ Sub 1枚（種別＋ドック位置＋同ドック内の比率）。値で比較できるよう record。</summary>
public sealed record StageSub(PaneKind Kind, StageDock Dock, double Weight = 1);

/// <summary>
/// 配置モードの舞台状態（主役 1枚＋サブ 0..2枚）の不変スナップショット。
/// Sub が 0 件なら従来の単一ステージ、1件以上なら配置モード、という派生状態。
/// </summary>
public sealed record StageState(PaneKind Main, IReadOnlyList<StageSub> Subs)
{
    /// <summary>配置モード中か（サブが1件以上）。</summary>
    public bool ProgramActive => Subs.Count > 0;

    /// <summary>舞台に立っているペイン（主役＋サブ）。</summary>
    public IEnumerable<PaneKind> OnStage => new[] { Main }.Concat(Subs.Select(s => s.Kind));

    /// <summary>指定ペインが舞台に立っているか。</summary>
    public bool IsOnStage(PaneKind kind) => Main == kind || Subs.Any(s => s.Kind == kind);
}

/// <summary>ドラッグ元／ドロップ先の舞台スロット。袖（舞台外）・主役・既存サブ・新規サブ。</summary>
public abstract record StageSlot;

/// <summary>袖（舞台外）のペイン。</summary>
public sealed record WingSlot(PaneKind Kind) : StageSlot;

/// <summary>主役スロット。</summary>
public sealed record MainSlot : StageSlot;

/// <summary>既存のサブスロット（インデックス指定）。</summary>
public sealed record SubSlot(int Index) : StageSlot;

/// <summary>新規サブスロット（このドックへ迎え入れる）。</summary>
public sealed record NewSubSlot(StageDock Dock) : StageSlot;

/// <summary>
/// 配置モードの舞台状態に対する純粋操作（Ctrl+T 巡回・袖/主役/サブの入れ替え）。
/// UI に触れないので単体テストできる（<see cref="PaneLayoutTree"/> と同方針）。
/// </summary>
public static class StageProgramLogic
{
    /// <summary>サブの最大枚数。</summary>
    public const int MaxSubs = 2;

    /// <summary>
    /// Ctrl+T：主役は固定したまま、末尾のサブを舞台外ペインの輪で前後へ送る。
    /// 既に舞台に立っているペイン（主役・他のサブ）はスキップする。送り先が無ければ無変更。
    /// </summary>
    public static StageState NextSubCycle(StageState state, IReadOnlyList<PaneKind> order, int direction)
    {
        if (state.Subs.Count == 0)
            return state;

        var last = state.Subs[^1];
        // 末尾サブ以外の在台ペイン（主役＋先行サブ）は飛ばす。
        var skip = new HashSet<PaneKind>(state.Subs.Take(state.Subs.Count - 1).Select(s => s.Kind))
        {
            state.Main
        };
        var next = NextAvailable(order, last.Kind, direction, skip);
        if (next is null)
            return state;

        var subs = state.Subs.ToList();
        subs[^1] = last with { Kind = next.Value };
        return state with { Subs = subs };
    }

    /// <summary>
    /// ドラッグ元 <paramref name="from"/> の中身を <paramref name="to"/> へ立て、押し出された中身を
    /// <paramref name="from"/> へ戻す（＝入れ替え）。袖から来た分の押し出しは舞台から降りる。
    /// </summary>
    public static StageState ApplySwap(StageState state, StageSlot from, StageSlot to)
    {
        var drag = Occupant(state, from);
        if (drag is null)
            return state;

        var main = state.Main;
        var subs = state.Subs.ToList();

        switch (to)
        {
            case MainSlot:
            {
                var displaced = main;
                main = drag.Value;
                PutBack(from, displaced, ref main, subs);
                break;
            }
            case SubSlot t when t.Index >= 0 && t.Index < subs.Count:
            {
                var displaced = subs[t.Index].Kind;
                subs[t.Index] = subs[t.Index] with { Kind = drag.Value };
                PutBack(from, displaced, ref main, subs);
                break;
            }
            case NewSubSlot ns:
            {
                // 新しいサブとして迎える。サブの移動（from がサブ）なら元の位置から外す。
                if (from is SubSlot fs && fs.Index >= 0 && fs.Index < subs.Count)
                    subs.RemoveAt(fs.Index);
                if (subs.All(s => s.Kind != drag.Value))
                    subs.Add(new StageSub(drag.Value, ns.Dock));
                while (subs.Count > MaxSubs)
                    subs.RemoveAt(0);
                break;
            }
        }

        return state with { Main = main, Subs = subs };
    }

    /// <summary>袖（舞台外）ペインを新しいサブとして迎える（配置モードへの突入も兼ねる）。</summary>
    public static StageState AddSub(StageState state, PaneKind kind, StageDock dock)
    {
        if (state.Main == kind)
            return state;
        var subs = state.Subs.ToList();
        var existing = subs.FindIndex(s => s.Kind == kind);
        if (existing >= 0)
        {
            subs[existing] = subs[existing] with { Dock = dock };   // ドック変更のみ
        }
        else
        {
            subs.Add(new StageSub(kind, dock));
            while (subs.Count > MaxSubs)
                subs.RemoveAt(0);
        }
        return state with { Subs = subs };
    }

    /// <summary>サブを降ろす。0 件になれば <see cref="StageState.ProgramActive"/> が false に戻る（＝配置終了）。</summary>
    public static StageState RemoveSub(StageState state, PaneKind kind)
        => state with { Subs = state.Subs.Where(s => s.Kind != kind).ToList() };

    private static PaneKind? Occupant(StageState state, StageSlot slot) => slot switch
    {
        WingSlot w => w.Kind,
        MainSlot => state.Main,
        SubSlot s when s.Index >= 0 && s.Index < state.Subs.Count => state.Subs[s.Index].Kind,
        _ => null
    };

    /// <summary>押し出された <paramref name="displaced"/> をドラッグ元へ戻す。袖から来たなら舞台外へ。</summary>
    private static void PutBack(StageSlot from, PaneKind displaced, ref PaneKind main, List<StageSub> subs)
    {
        switch (from)
        {
            case MainSlot:
                main = displaced;
                break;
            case SubSlot fs when fs.Index >= 0 && fs.Index < subs.Count:
                subs[fs.Index] = subs[fs.Index] with { Kind = displaced };
                break;
            // WingSlot：押し出された分は舞台から降りる（戻さない）。
        }
    }

    private static PaneKind? NextAvailable(
        IReadOnlyList<PaneKind> order, PaneKind current, int direction, ISet<PaneKind> skip)
    {
        if (order.Count == 0)
            return null;
        var start = order.ToList().IndexOf(current);
        if (start < 0)
            start = 0;
        var step = direction >= 0 ? 1 : -1;
        for (var n = 1; n <= order.Count; n++)
        {
            var idx = ((start + step * n) % order.Count + order.Count) % order.Count;
            var cand = order[idx];
            if (cand == current || skip.Contains(cand))
                continue;
            return cand;
        }
        return null;
    }
}
