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
