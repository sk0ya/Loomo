using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ペインレイアウトツリー（<see cref="PaneLayoutTree"/>）の純粋操作の検証。
/// 直近の不具合（移動の巻き戻り・非表示→再表示でのレイアウト崩れ）を回帰テストとして固定する。
/// </summary>
public class PaneLayoutTreeTests
{
    private static PaneLeaf Leaf(PaneKind kind, double weight = 1, bool hidden = false)
        => new() { Kind = kind, Weight = weight, Hidden = hidden };

    private static PaneSplit Split(SplitKind orientation, params PaneNode[] children)
    {
        var split = new PaneSplit { Orientation = orientation };
        split.Children.AddRange(children);
        return split;
    }

    /// <summary>既定レイアウト相当：[Editor | Browser] / Terminal / AI。</summary>
    private static PaneNode DefaultishTree()
        => Split(SplitKind.Rows,
            Split(SplitKind.Columns, Leaf(PaneKind.Editor), Leaf(PaneKind.Browser)),
            Leaf(PaneKind.Terminal),
            Leaf(PaneKind.Ai));

    // ===== AllLeaves / FindLeaf / FindParent =====

    [Fact]
    public void AllLeaves_enumerates_leaves_in_layout_order()
    {
        var kinds = PaneLayoutTree.AllLeaves(DefaultishTree()).Select(l => l.Kind).ToList();
        Assert.Equal(new[] { PaneKind.Editor, PaneKind.Browser, PaneKind.Terminal, PaneKind.Ai }, kinds);
    }

    [Fact]
    public void FindParent_returns_direct_parent_and_null_for_root()
    {
        var editor = Leaf(PaneKind.Editor);
        var top = Split(SplitKind.Columns, editor, Leaf(PaneKind.Browser));
        var root = Split(SplitKind.Rows, top, Leaf(PaneKind.Terminal));

        Assert.Same(top, PaneLayoutTree.FindParent(root, editor));
        Assert.Null(PaneLayoutTree.FindParent(root, root));
    }

    // ===== IsNodeVisible =====

    [Fact]
    public void IsNodeVisible_is_false_when_all_descendant_leaves_are_hidden()
    {
        var split = Split(SplitKind.Columns, Leaf(PaneKind.Editor, hidden: true), Leaf(PaneKind.Browser, hidden: true));
        Assert.False(PaneLayoutTree.IsNodeVisible(split));

        split.Children.Add(Leaf(PaneKind.Terminal));
        Assert.True(PaneLayoutTree.IsNodeVisible(split));
    }

    // ===== Normalize =====

    [Fact]
    public void Normalize_collapses_single_child_split_and_keeps_outer_weight()
    {
        var editor = Leaf(PaneKind.Editor, weight: 3);
        var lone = Split(SplitKind.Columns, editor);
        lone.Weight = 2;

        var result = PaneLayoutTree.Normalize(lone);

        Assert.Same(editor, result);
        Assert.Equal(2, editor.Weight); // スプリットの外側比率を子が引き継ぐ
    }

    [Fact]
    public void Normalize_flattens_same_orientation_preserving_ratios()
    {
        // Rows[ Rows[a(3), b(1)](weight=2), c(1) ] → Rows[a(1.5), b(0.5), c(1)]
        var a = Leaf(PaneKind.Editor, weight: 3);
        var b = Leaf(PaneKind.Browser, weight: 1);
        var inner = Split(SplitKind.Rows, a, b);
        inner.Weight = 2;
        var c = Leaf(PaneKind.Terminal, weight: 1);
        var root = Split(SplitKind.Rows, inner, c);

        var result = Assert.IsType<PaneSplit>(PaneLayoutTree.Normalize(root));

        Assert.Equal(new PaneNode[] { a, b, c }, result.Children);
        Assert.Equal(1.5, a.Weight);
        Assert.Equal(0.5, b.Weight);
        Assert.Equal(1, c.Weight);
    }

    [Fact]
    public void Normalize_removes_empty_split_and_keeps_hidden_leaves()
    {
        var hidden = Leaf(PaneKind.EditorSupport, hidden: true);
        var root = Split(SplitKind.Rows,
            Split(SplitKind.Columns), // 空スプリット→除去される
            Split(SplitKind.Columns, Leaf(PaneKind.Editor), hidden),
            Leaf(PaneKind.Terminal));

        var result = Assert.IsType<PaneSplit>(PaneLayoutTree.Normalize(root));

        Assert.Equal(2, result.Children.Count);
        // 非表示リーフはツリーに残る（再表示で元の位置・比率に戻すため）
        Assert.Contains(hidden, PaneLayoutTree.AllLeaves(result));
    }

    // ===== RemoveNode / InsertRelative =====

    [Fact]
    public void RemoveNode_detaches_leaf_and_returns_null_when_removing_root()
    {
        var editor = Leaf(PaneKind.Editor);
        var root = Split(SplitKind.Rows, editor, Leaf(PaneKind.Terminal));

        Assert.Same(root, PaneLayoutTree.RemoveNode(root, editor));
        Assert.DoesNotContain(editor, PaneLayoutTree.AllLeaves(root));

        var single = Leaf(PaneKind.Ai);
        Assert.Null(PaneLayoutTree.RemoveNode(single, single));
    }

    [Fact]
    public void InsertRelative_inserts_as_sibling_when_orientation_matches()
    {
        var editor = Leaf(PaneKind.Editor);
        var browser = Leaf(PaneKind.Browser);
        var root = Split(SplitKind.Columns, editor, browser);
        var ai = Leaf(PaneKind.Ai);

        var result = PaneLayoutTree.InsertRelative(root, ai, browser, DropZone.Left);

        Assert.Same(root, result);
        Assert.Equal(new PaneNode[] { editor, ai, browser }, root.Children);
    }

    [Fact]
    public void InsertRelative_sibling_splits_target_weight_in_half()
    {
        // CaptureLayoutSizes 後は実ピクセル幅が重みに入る想定（例：editor=800, browser=400）。
        // editor の右へ同方向で挿し込むと、editor の取り分を新ペインと半分ずつ（400/400）に割り、
        // 隣の browser はそのまま。重み 1 のまま挿すと新ペインが極端に細くなる回帰を防ぐ。
        var editor = Leaf(PaneKind.Editor, weight: 800);
        var browser = Leaf(PaneKind.Browser, weight: 400);
        var root = Split(SplitKind.Columns, editor, browser);
        var ai = Leaf(PaneKind.Ai, weight: 1);

        var result = PaneLayoutTree.InsertRelative(root, ai, editor, DropZone.Right);

        Assert.Same(root, result);
        Assert.Equal(new PaneNode[] { editor, ai, browser }, root.Children);
        Assert.Equal(400, editor.Weight);
        Assert.Equal(400, ai.Weight);
        Assert.Equal(400, browser.Weight); // 隣の兄弟は影響を受けない
    }

    [Fact]
    public void InsertRelative_wraps_target_when_orientation_differs_and_inherits_weight()
    {
        var editor = Leaf(PaneKind.Editor, weight: 2);
        var browser = Leaf(PaneKind.Browser);
        var root = Split(SplitKind.Columns, editor, browser);
        var ai = Leaf(PaneKind.Ai);

        // 列の中の editor だけを上下分割する
        var result = PaneLayoutTree.InsertRelative(root, ai, editor, DropZone.Below);

        Assert.Same(root, result);
        var wrap = Assert.IsType<PaneSplit>(root.Children[0]);
        Assert.Equal(SplitKind.Rows, wrap.Orientation);
        Assert.Equal(2, wrap.Weight);   // 包んだスプリットが列内の比率を引き継ぐ
        Assert.Equal(1, editor.Weight); // 包まれた側は等分に戻る
        Assert.Equal(new PaneNode[] { editor, ai }, wrap.Children);
    }

    [Fact]
    public void InsertRelative_replaces_root_when_target_is_root()
    {
        var editor = Leaf(PaneKind.Editor);
        var ai = Leaf(PaneKind.Ai);

        var result = Assert.IsType<PaneSplit>(PaneLayoutTree.InsertRelative(editor, ai, editor, DropZone.Right));

        Assert.Equal(SplitKind.Columns, result.Orientation);
        Assert.Equal(new PaneNode[] { editor, ai }, result.Children);
    }

    // ===== ResolveSpanTarget / span 挿入 =====

    [Fact]
    public void ResolveSpanTarget_climbs_to_perpendicular_ancestor()
    {
        // Rows[ Columns[Editor, Browser], Terminal ] で Editor の下端は Columns 全体の下端＝
        // Editor 単体でなく Columns[Editor,Browser] を返す（左右2ペインの下へフル幅で落とすため）。
        var editor = Leaf(PaneKind.Editor);
        var top = Split(SplitKind.Columns, editor, Leaf(PaneKind.Browser));
        var root = Split(SplitKind.Rows, top, Leaf(PaneKind.Terminal));

        Assert.Same(top, PaneLayoutTree.ResolveSpanTarget(root, editor, DropZone.Below));
    }

    [Fact]
    public void ResolveSpanTarget_returns_leaf_when_parent_matches_direction()
    {
        // 親が既に望む方向（左右）なら祖先へ遡らず、単体ペインへの挿入と同じ＝リーフ自身を返す。
        var editor = Leaf(PaneKind.Editor);
        var root = Split(SplitKind.Columns, editor, Leaf(PaneKind.Browser));

        Assert.Same(editor, PaneLayoutTree.ResolveSpanTarget(root, editor, DropZone.Left));
    }

    [Fact]
    public void MoveInTree_span_inserts_full_width_below_both_columns()
    {
        // ［Editor | Browser］が並ぶ列の下（スパン）へ Ai を移す＝列全体の下にフル幅で挿入され、
        // Rows[ Columns[Editor,Browser], Ai ] になる（左右2ペインの下に落とせなかった不具合の回帰）。
        var root = Split(SplitKind.Columns, Leaf(PaneKind.Editor), Leaf(PaneKind.Browser), Leaf(PaneKind.Ai));

        var result = Assert.IsType<PaneSplit>(
            PaneLayoutTree.MoveInTree(root, PaneKind.Ai, PaneKind.Editor, DropZone.Below, span: true));

        Assert.Equal(SplitKind.Rows, result.Orientation);
        var top = Assert.IsType<PaneSplit>(result.Children[0]);
        Assert.Equal(SplitKind.Columns, top.Orientation);
        Assert.Equal(new[] { PaneKind.Editor, PaneKind.Browser },
            top.Children.Cast<PaneLeaf>().Select(l => l.Kind));
        Assert.Equal(PaneKind.Ai, Assert.IsType<PaneLeaf>(result.Children[1]).Kind);
    }

    [Fact]
    public void MoveInTree_without_span_splits_only_target_column()
    {
        // スパン無しは従来どおりターゲット列だけを上下分割する（回帰防止）。
        var root = Split(SplitKind.Columns, Leaf(PaneKind.Editor), Leaf(PaneKind.Browser), Leaf(PaneKind.Ai));

        var result = Assert.IsType<PaneSplit>(
            PaneLayoutTree.MoveInTree(root, PaneKind.Ai, PaneKind.Editor, DropZone.Below, span: false));

        Assert.Equal(SplitKind.Columns, result.Orientation);
        var left = Assert.IsType<PaneSplit>(result.Children[0]);
        Assert.Equal(SplitKind.Rows, left.Orientation);
        Assert.Equal(new[] { PaneKind.Editor, PaneKind.Ai },
            left.Children.Cast<PaneLeaf>().Select(l => l.Kind));
    }

    // ===== MoveInTree =====

    [Fact]
    public void MoveInTree_moves_leaf_to_target_edge()
    {
        var root = DefaultishTree();

        var result = PaneLayoutTree.MoveInTree(root, PaneKind.Terminal, PaneKind.Editor, DropZone.Left);

        var top = Assert.IsType<PaneSplit>(Assert.IsType<PaneSplit>(result).Children[0]);
        Assert.Equal(SplitKind.Columns, top.Orientation);
        Assert.Equal(PaneKind.Terminal, Assert.IsType<PaneLeaf>(top.Children[0]).Kind);
        Assert.Equal(PaneKind.Editor, Assert.IsType<PaneLeaf>(top.Children[1]).Kind);
        // 全リーフが保存される（消えない・増えない）
        Assert.Equal(4, PaneLayoutTree.AllLeaves(result).Count());
    }

    [Fact]
    public void MoveInTree_is_noop_when_source_or_target_missing()
    {
        var root = DefaultishTree();
        Assert.Same(root, PaneLayoutTree.MoveInTree(root, PaneKind.Git, PaneKind.Editor, DropZone.Left));
        Assert.Same(root, PaneLayoutTree.MoveInTree(root, PaneKind.Editor, PaneKind.Editor, DropZone.Left));
    }

    [Fact]
    public void MoveInTree_then_move_back_restores_leaf_set()
    {
        var root = DefaultishTree();
        var moved = PaneLayoutTree.MoveInTree(root!, PaneKind.Ai, PaneKind.Editor, DropZone.Above)!;
        var restored = PaneLayoutTree.MoveInTree(moved, PaneKind.Ai, PaneKind.Terminal, DropZone.Below)!;

        var kinds = PaneLayoutTree.AllLeaves(restored).Select(l => l.Kind).ToHashSet();
        Assert.Equal(new HashSet<PaneKind> { PaneKind.Editor, PaneKind.Browser, PaneKind.Terminal, PaneKind.Ai }, kinds);
    }

    // ===== AddLeafAtBottom =====

    [Fact]
    public void AddLeafAtBottom_appends_to_rows_root_or_wraps_other_roots()
    {
        var rows = Split(SplitKind.Rows, Leaf(PaneKind.Editor));
        var git = Leaf(PaneKind.Git);
        Assert.Same(rows, PaneLayoutTree.AddLeafAtBottom(rows, git));
        Assert.Same(git, rows.Children[^1]);

        var columns = Split(SplitKind.Columns, Leaf(PaneKind.Editor), Leaf(PaneKind.Browser));
        columns.Weight = 3;
        var diff = Leaf(PaneKind.Diff);
        var wrapped = Assert.IsType<PaneSplit>(PaneLayoutTree.AddLeafAtBottom(columns, diff));
        Assert.Equal(SplitKind.Rows, wrapped.Orientation);
        Assert.Equal(3, wrapped.Weight);  // 外側の比率は包んだ行スプリットへ
        Assert.Equal(1, columns.Weight);
        Assert.Equal(new PaneNode[] { columns, diff }, wrapped.Children);

        Assert.Same(git, PaneLayoutTree.AddLeafAtBottom(null, git));
    }

    // ===== TopRow / Rightmost / Leftmost（サブ＝右上 判定の土台） =====

    [Fact]
    public void TopRow_returns_first_visible_row_and_ignores_lower_rows()
    {
        // 既定相当：Rows[ Columns[Editor,Browser], Terminal, Ai ] の上段は Columns[Editor,Browser]。
        var top = Split(SplitKind.Columns, Leaf(PaneKind.Editor), Leaf(PaneKind.Browser));
        var root = Split(SplitKind.Rows, top, Leaf(PaneKind.Terminal), Leaf(PaneKind.Ai));

        Assert.Same(top, PaneLayoutTree.TopRow(root));
    }

    [Fact]
    public void Rightmost_and_Leftmost_pick_top_row_edges_skipping_hidden()
    {
        // 上段 Columns[Editor, EditorSupport(hidden), Browser]：左端=Editor、右端=Browser（非表示は飛ばす）。
        var top = Split(SplitKind.Columns,
            Leaf(PaneKind.Editor),
            Leaf(PaneKind.EditorSupport, hidden: true),
            Leaf(PaneKind.Browser));
        var root = Split(SplitKind.Rows, top, Leaf(PaneKind.Terminal), Leaf(PaneKind.Ai));

        var topRow = PaneLayoutTree.TopRow(root);
        Assert.Equal(PaneKind.Browser, PaneLayoutTree.RightmostVisibleLeaf(topRow)!.Kind);
        Assert.Equal(PaneKind.Editor, PaneLayoutTree.LeftmostVisibleLeaf(topRow)!.Kind);
    }

    [Fact]
    public void Rightmost_never_returns_a_lower_row_pane()
    {
        // 回帰：矩形フォールバックが下段（Ai）を「右上」と誤認していた不具合の防止。
        // 上段が単一 Editor でも、右端は Ai/Terminal ではなく Editor（＝上段の中だけを見る）。
        var root = Split(SplitKind.Rows, Leaf(PaneKind.Editor), Leaf(PaneKind.Terminal), Leaf(PaneKind.Ai));
        var topRow = PaneLayoutTree.TopRow(root);

        Assert.Equal(PaneKind.Editor, PaneLayoutTree.RightmostVisibleLeaf(topRow)!.Kind);
        Assert.Equal(PaneKind.Editor, PaneLayoutTree.LeftmostVisibleLeaf(topRow)!.Kind);
    }

    [Fact]
    public void Sub_swap_places_target_at_top_right_replacing_the_right_pane()
    {
        // sub モードの「右上と入れ替え」を PlaceInTree(center) と同じ手順で再現：
        // 右上リーフの左へ対象を挿し、右上リーフを外す＝対象が右上の位置を引き継ぐ。
        var root = DefaultishTree(); // Rows[ Columns[Editor,Browser], Terminal, Ai ]
        var sub = PaneLayoutTree.RightmostVisibleLeaf(PaneLayoutTree.TopRow(root))!; // Browser
        Assert.Equal(PaneKind.Browser, sub.Kind);

        var diff = Leaf(PaneKind.Diff);
        var after = PaneLayoutTree.InsertRelative(root, diff, sub, DropZone.Left);
        after = PaneLayoutTree.RemoveNode(after, sub);
        after = PaneLayoutTree.Normalize(after);

        // 上段は [Editor, Diff]、Diff が右上、Browser は消える。
        var topRow = Assert.IsType<PaneSplit>(PaneLayoutTree.TopRow(after));
        Assert.Equal(SplitKind.Columns, topRow.Orientation);
        Assert.Equal(new[] { PaneKind.Editor, PaneKind.Diff },
            topRow.Children.Cast<PaneLeaf>().Select(l => l.Kind));
        Assert.Equal(PaneKind.Diff, PaneLayoutTree.RightmostVisibleLeaf(PaneLayoutTree.TopRow(after))!.Kind);
    }

    [Fact]
    public void Sub_insert_adds_target_to_the_right_when_top_row_is_single_pane()
    {
        // 上段が横1枚（Editor のみ）＝メイン＝サブ。sub モードは右へ追加してサブを作る。
        var root = Split(SplitKind.Rows, Leaf(PaneKind.Editor), Leaf(PaneKind.Terminal));
        var main = PaneLayoutTree.LeftmostVisibleLeaf(PaneLayoutTree.TopRow(root))!; // Editor
        Assert.Same(PaneLayoutTree.RightmostVisibleLeaf(PaneLayoutTree.TopRow(root)), main); // 単一なので左右一致

        var diff = Leaf(PaneKind.Diff);
        var after = PaneLayoutTree.Normalize(PaneLayoutTree.InsertRelative(root, diff, main, DropZone.Right));

        // 上段は [Editor | Diff]、Diff が右上。
        var topRow = Assert.IsType<PaneSplit>(PaneLayoutTree.TopRow(after));
        Assert.Equal(SplitKind.Columns, topRow.Orientation);
        Assert.Equal(new[] { PaneKind.Editor, PaneKind.Diff },
            topRow.Children.Cast<PaneLeaf>().Select(l => l.Kind));
        Assert.Equal(PaneKind.Diff, PaneLayoutTree.RightmostVisibleLeaf(PaneLayoutTree.TopRow(after))!.Kind);
    }

    // ===== ToSnapshot / BuildFromSnapshot =====

    [Fact]
    public void Snapshot_roundtrip_preserves_structure_weights_and_hidden()
    {
        var root = Split(SplitKind.Rows,
            Split(SplitKind.Columns,
                Leaf(PaneKind.Editor, weight: 2),
                Leaf(PaneKind.EditorSupport, hidden: true),
                Leaf(PaneKind.Browser)),
            Leaf(PaneKind.Terminal, weight: 0.5));
        root.Children[0].Weight = 2;

        var rebuilt = PaneLayoutTree.BuildFromSnapshot(
            PaneLayoutTree.ToSnapshot(root), new HashSet<PaneKind>(), _ => true);

        var split = Assert.IsType<PaneSplit>(rebuilt);
        Assert.Equal(SplitKind.Rows, split.Orientation);
        var top = Assert.IsType<PaneSplit>(split.Children[0]);
        Assert.Equal(SplitKind.Columns, top.Orientation);
        Assert.Equal(2, top.Weight);

        var leaves = PaneLayoutTree.AllLeaves(rebuilt).ToList();
        Assert.Equal(4, leaves.Count);
        var support = Assert.Single(leaves, l => l.Kind == PaneKind.EditorSupport);
        Assert.True(support.Hidden);
        Assert.Equal(2, Assert.Single(leaves, l => l.Kind == PaneKind.Editor).Weight);
        Assert.Equal(0.5, Assert.Single(leaves, l => l.Kind == PaneKind.Terminal).Weight);
    }

    [Fact]
    public void BuildFromSnapshot_drops_unknown_and_duplicate_kinds()
    {
        var snapshot = PaneLayoutTree.ToSnapshot(Split(SplitKind.Rows,
            Leaf(PaneKind.Editor),
            Leaf(PaneKind.Editor),   // 重複→捨てる
            Leaf(PaneKind.Trace),    // 未知（isKnownKind=false）→捨てる
            Leaf(PaneKind.Terminal)));

        var rebuilt = PaneLayoutTree.BuildFromSnapshot(
            snapshot, new HashSet<PaneKind>(), k => k != PaneKind.Trace);

        var kinds = PaneLayoutTree.AllLeaves(rebuilt).Select(l => l.Kind).ToList();
        Assert.Equal(new[] { PaneKind.Editor, PaneKind.Terminal }, kinds);
    }

    [Fact]
    public void BuildFromSnapshot_collapses_to_single_leaf_when_others_are_dropped()
    {
        var snapshot = PaneLayoutTree.ToSnapshot(Split(SplitKind.Rows,
            Leaf(PaneKind.Editor),
            Leaf(PaneKind.Trace)));
        snapshot.Weight = 4;

        var rebuilt = PaneLayoutTree.BuildFromSnapshot(
            snapshot, new HashSet<PaneKind>(), k => k != PaneKind.Trace);

        var leaf = Assert.IsType<PaneLeaf>(rebuilt);
        Assert.Equal(PaneKind.Editor, leaf.Kind);
        Assert.Equal(4, leaf.Weight); // 畳んだスプリットの比率を引き継ぐ
    }
}
