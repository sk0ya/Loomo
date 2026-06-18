using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Layout;

/// <summary>ドロップ・ナビゲーションの方向（ターゲットのどの辺か）。</summary>
public enum DropZone { Left, Right, Above, Below }

/// <summary>分割方向。Rows＝上下に積む、Columns＝左右に並べる。</summary>
public enum SplitKind { Rows, Columns }

/// <summary>レイアウトツリーの1ノード（リーフ＝ペイン、スプリット＝入れ子の行/列）。</summary>
public abstract class PaneNode
{
    /// <summary>親スプリット内での star 比率。</summary>
    public double Weight { get; set; } = 1;
    /// <summary>直近の描画で割り当てられた Grid トラック番号（未描画は -1）。サイズ取り込み用。</summary>
    public int TrackIndex { get; set; } = -1;
}

/// <summary>リーフ＝1ペイン。</summary>
public sealed class PaneLeaf : PaneNode
{
    public PaneKind Kind { get; init; }
    /// <summary>非表示中か。true でもツリーには残し、再表示で元の位置・比率へ戻す。</summary>
    public bool Hidden { get; set; }
}

/// <summary>スプリット＝入れ子の行（上下）または列（左右）。</summary>
public sealed class PaneSplit : PaneNode
{
    public SplitKind Orientation { get; init; }
    public List<PaneNode> Children { get; } = new();
    /// <summary>再構築のたびに生成される Grid。サイズ取り込み用の一時参照。</summary>
    public Grid? Host { get; set; }
}

/// <summary>
/// ペインレイアウトツリーの純粋操作（探索・移動・正規化・スナップショット変換）。
/// UI（Grid 構築・サイズ取り込み）には触れないので単体テストできる。
/// </summary>
public static class PaneLayoutTree
{
    /// <summary>ツリー内のすべてのリーフ（ペイン）を列挙する。</summary>
    public static IEnumerable<PaneLeaf> AllLeaves(PaneNode? node)
    {
        if (node is PaneLeaf leaf)
        {
            yield return leaf;
        }
        else if (node is PaneSplit split)
        {
            foreach (var child in split.Children)
                foreach (var l in AllLeaves(child))
                    yield return l;
        }
    }

    /// <summary>指定種別のリーフを返す（無ければ null）。</summary>
    public static PaneLeaf? FindLeaf(PaneNode? root, PaneKind kind)
        => AllLeaves(root).FirstOrDefault(l => l.Kind == kind);

    /// <summary>指定ノードを直接の子に持つスプリットを返す（ルートなら null）。</summary>
    public static PaneSplit? FindParent(PaneNode? current, PaneNode target)
    {
        if (current is not PaneSplit split)
            return null;
        if (split.Children.Contains(target))
            return split;
        foreach (var child in split.Children)
        {
            var found = FindParent(child, target);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>ノード（リーフ／スプリット）に可視なペインが含まれるか。</summary>
    public static bool IsNodeVisible(PaneNode node) => node switch
    {
        PaneLeaf leaf => !leaf.Hidden,
        PaneSplit split => split.Children.Any(IsNodeVisible),
        _ => false
    };

    /// <summary>
    /// ツリーを正規化する：空スプリットを除去し、子が1つのスプリットを畳み、
    /// 同方向に入れ子になったスプリットをフラット化する。
    /// </summary>
    public static PaneNode? Normalize(PaneNode? node)
    {
        if (node is not PaneSplit split)
            return node;

        var kids = new List<PaneNode>();
        foreach (var child in split.Children)
        {
            var n = Normalize(child);
            if (n is null)
                continue;
            if (n is PaneSplit inner && inner.Orientation == split.Orientation)
                AddFlattenedChildren(kids, inner); // 同方向は比率を保ってフラット化
            else
                kids.Add(n);
        }

        if (kids.Count == 0)
            return null;
        if (kids.Count == 1)
        {
            kids[0].Weight = split.Weight;
            return kids[0];
        }

        split.Children.Clear();
        split.Children.AddRange(kids);
        return split;
    }

    private static void AddFlattenedChildren(List<PaneNode> destination, PaneSplit inner)
    {
        var total = inner.Children.Sum(c => c.Weight > 0 ? c.Weight : 1);
        var scale = total > 0 ? (inner.Weight > 0 ? inner.Weight : 1) / total : 1;
        foreach (var child in inner.Children)
        {
            var weight = child.Weight > 0 ? child.Weight : 1;
            child.Weight = weight * scale;
            destination.Add(child);
        }
    }

    /// <summary>ノードを親スプリットから取り外し、新しいルートを返す（畳み込みは Normalize に任せる）。</summary>
    public static PaneNode? RemoveNode(PaneNode? root, PaneNode node)
    {
        var parent = FindParent(root, node);
        if (parent is null)
            return ReferenceEquals(root, node) ? null : root;
        parent.Children.Remove(node);
        return root;
    }

    /// <summary>
    /// <paramref name="node"/> を <paramref name="target"/> の指定した辺へ挿入し、新しいルートを返す。
    /// 望む方向が target の親スプリットと一致すれば兄弟として差し込み、
    /// 異なれば target を新しいスプリットで包む（＝列の片方だけを上下分割できる）。
    /// </summary>
    public static PaneNode? InsertRelative(PaneNode? root, PaneNode node, PaneLeaf target, DropZone zone)
    {
        var wantColumns = zone is DropZone.Left or DropZone.Right;
        var before = zone is DropZone.Left or DropZone.Above;
        var desired = wantColumns ? SplitKind.Columns : SplitKind.Rows;
        var parent = FindParent(root, target);

        if (parent is not null && parent.Orientation == desired)
        {
            var index = parent.Children.IndexOf(target);
            parent.Children.Insert(before ? index : index + 1, node);
            return root;
        }

        // target を内包する新しいスプリットへ置き換える。
        var split = new PaneSplit { Orientation = desired, Weight = target.Weight };
        target.Weight = 1;
        if (before)
        {
            split.Children.Add(node);
            split.Children.Add(target);
        }
        else
        {
            split.Children.Add(target);
            split.Children.Add(node);
        }

        if (parent is null)
            return split;
        parent.Children[parent.Children.IndexOf(target)] = split;
        return root;
    }

    /// <summary>指定ツリー上でペイン移動（取り外し→指定辺へ挿入→正規化）を行い、新しいルートを返す。</summary>
    public static PaneNode? MoveInTree(PaneNode root, PaneKind source, PaneKind target, DropZone zone)
    {
        var sourceLeaf = FindLeaf(root, source);
        var targetLeaf = FindLeaf(root, target);
        if (sourceLeaf is null || targetLeaf is null || ReferenceEquals(sourceLeaf, targetLeaf))
            return root;

        var newRoot = RemoveNode(root, sourceLeaf);
        sourceLeaf.Weight = 1;
        return Normalize(InsertRelative(newRoot, sourceLeaf, targetLeaf, zone));
    }

    /// <summary>指定ツリーの最下段の新しい行としてリーフを追加し、新しいルートを返す。
    /// 既存ノードを行スプリットで包む場合は外側の重み（親スプリット内の比率）を引き継ぐ。</summary>
    public static PaneNode AddLeafAtBottom(PaneNode? root, PaneLeaf leaf)
    {
        if (root is null)
            return leaf;
        if (root is PaneSplit { Orientation: SplitKind.Rows } rows)
        {
            rows.Children.Add(leaf);
            return rows;
        }
        var wrap = new PaneSplit { Orientation = SplitKind.Rows, Weight = root.Weight };
        root.Weight = 1;
        wrap.Children.Add(root);
        wrap.Children.Add(leaf);
        return wrap;
    }

    /// <summary>
    /// 2つのスナップショットが同じ配置か（ペイン種別・表示/非表示・行列構造が一致するか）を判定する。
    /// 比率（<see cref="PaneNodeSnapshot.Weight"/>）は配置の同一性に含めない。Ctrl+T 巡回で
    /// 未保存配置を退避する際、保存レイアウトと同配置なら退避を省く（重複表示防止）ために使う。
    /// </summary>
    public static bool SnapshotsEquivalent(PaneNodeSnapshot a, PaneNodeSnapshot b)
    {
        var aLeaf = a.Children is not { Count: > 0 };
        var bLeaf = b.Children is not { Count: > 0 };
        if (aLeaf != bLeaf)
            return false;
        if (aLeaf)
            return a.Kind == b.Kind && a.Hidden == b.Hidden;

        if (a.Orientation != b.Orientation || a.Children.Count != b.Children.Count)
            return false;
        for (var i = 0; i < a.Children.Count; i++)
            if (!SnapshotsEquivalent(a.Children[i], b.Children[i]))
                return false;
        return true;
    }

    /// <summary>ツリーを永続化用スナップショットへ変換する。</summary>
    public static PaneNodeSnapshot ToSnapshot(PaneNode node)
    {
        if (node is PaneLeaf leaf)
            return new PaneNodeSnapshot { Weight = leaf.Weight, Kind = leaf.Kind, Hidden = leaf.Hidden };

        var split = (PaneSplit)node;
        return new PaneNodeSnapshot
        {
            Weight = split.Weight,
            Orientation = split.Orientation == SplitKind.Columns ? "Columns" : "Rows",
            Children = split.Children.Select(ToSnapshot).ToList()
        };
    }

    /// <summary>スナップショットからツリーを再構築する（重複・未知のペインは捨てる）。</summary>
    public static PaneNode? BuildFromSnapshot(PaneNodeSnapshot snap, HashSet<PaneKind> seen, Func<PaneKind, bool> isKnownKind)
    {
        if (snap.Children is { Count: > 0 })
        {
            var kids = new List<PaneNode>();
            foreach (var child in snap.Children)
            {
                var n = BuildFromSnapshot(child, seen, isKnownKind);
                if (n is not null)
                    kids.Add(n);
            }
            if (kids.Count == 0)
                return null;
            if (kids.Count == 1)
            {
                kids[0].Weight = snap.Weight > 0 ? snap.Weight : 1;
                return kids[0];
            }
            var split = new PaneSplit
            {
                Orientation = snap.Orientation == "Columns" ? SplitKind.Columns : SplitKind.Rows,
                Weight = snap.Weight > 0 ? snap.Weight : 1
            };
            split.Children.AddRange(kids);
            return split;
        }

        if (snap.Kind is { } kind && isKnownKind(kind) && seen.Add(kind))
            return new PaneLeaf { Kind = kind, Weight = snap.Weight > 0 ? snap.Weight : 1, Hidden = snap.Hidden };
        return null;
    }
}
