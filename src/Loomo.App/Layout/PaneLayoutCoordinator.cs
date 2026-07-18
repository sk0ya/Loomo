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
