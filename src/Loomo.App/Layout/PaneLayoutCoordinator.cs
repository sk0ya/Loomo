using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Layout;

/// <summary>ペインレイアウトの状態と WPF 非依存の変更操作を保持する。</summary>
public sealed class PaneLayoutCoordinator
{
    public PaneNode? Root { get; set; }

    public IEnumerable<PaneLeaf> Leaves(PaneNode? node = null)
        => PaneLayoutTree.AllLeaves(node ?? Root);

    public PaneLeaf? Find(PaneKind kind) => PaneLayoutTree.FindLeaf(Root, kind);

    public PaneSplit? FindParent(PaneNode target, PaneNode? current = null)
        => PaneLayoutTree.FindParent(current ?? Root, target);

    public void Normalize() => Root = PaneLayoutTree.Normalize(Root);

    public bool Move(PaneKind source, PaneKind target, DropZone zone, bool span = false)
    {
        if (Root is null || source == target || Find(source) is null || Find(target) is null)
            return false;
        Root = PaneLayoutTree.MoveInTree(Root, source, target, zone, span);
        return true;
    }

    public bool Place(PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false)
    {
        if (Root is null || dragged == target || Find(target) is null)
            return false;
        Root = Place(Root, dragged, target, center, zone, span);
        return true;
    }

    /// <summary>ペインを表示／非表示にし、必要なら末尾へ追加する。</summary>
    public bool SetVisible(PaneKind kind, bool visible, bool appendToLastColumn = false)
    {
        var leaf = Find(kind);
        if (visible == (leaf is { Hidden: false }))
            return false;

        if (visible)
        {
            if (leaf is null)
            {
                var added = new PaneLeaf { Kind = kind };
                if (appendToLastColumn && Root is PaneSplit { Orientation: SplitKind.Columns } columns
                    && columns.Children.Count > 0)
                    columns.Children[^1] = PaneLayoutTree.AddLeafAtBottom(columns.Children[^1], added);
                else
                    Root = PaneLayoutTree.AddLeafAtBottom(Root, added);
            }
            else
                leaf.Hidden = false;
        }
        else
            leaf!.Hidden = true;

        Root = PaneLayoutTree.Normalize(Root);
        return true;
    }

    /// <summary>スパン最大化前の退避ツリーにも、表示状態の変更を反映する。</summary>
    public static PaneNode? SetVisibleOnSavedTree(PaneNode? root, PaneKind kind, bool visible)
    {
        var leaf = PaneLayoutTree.FindLeaf(root, kind);
        if (leaf is null)
        {
            if (!visible)
                return root;
            root = PaneLayoutTree.AddLeafAtBottom(root, new PaneLeaf { Kind = kind });
        }
        else
            leaf.Hidden = !visible;

        return root;
    }

    public static PaneNode? Place(
        PaneNode? root, PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false)
    {
        if (root is null)
            return null;
        var targetLeaf = PaneLayoutTree.FindLeaf(root, target);
        if (targetLeaf is null)
            return root;

        if (PaneLayoutTree.FindLeaf(root, dragged) is { } existing)
            root = PaneLayoutTree.RemoveNode(root, existing);

        if (center)
        {
            var targetWeight = targetLeaf.Weight > 0 ? targetLeaf.Weight : 1;
            var leaf = new PaneLeaf { Kind = dragged, Weight = targetWeight };
            root = PaneLayoutTree.InsertRelative(root, leaf, targetLeaf, DropZone.Left);
            root = PaneLayoutTree.RemoveNode(root, targetLeaf);
            leaf.Weight = targetWeight;
        }
        else
        {
            var insertZone = zone ?? DropZone.Right;
            PaneNode insertTarget = span
                ? PaneLayoutTree.ResolveSpanTarget(root, targetLeaf, insertZone)
                : targetLeaf;
            root = PaneLayoutTree.InsertRelative(
                root, new PaneLeaf { Kind = dragged }, insertTarget, insertZone);
        }
        return PaneLayoutTree.Normalize(root);
    }
}
