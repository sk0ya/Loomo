
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: レイアウトツリーの変更操作（ペイン移動・袖からの配置・入れ替え・挿入・除去）。 ツリーの構築/描画は ShellWindow.PaneLayout.cs、ドラッグ操作は ShellWindow.PaneDrag.cs。</summary>
public partial class ShellWindow {
    private void MovePane(PaneKind source, PaneKind target, DropZone zone, bool span = false) {
        if (source == target)
            return;

        var sourceLeaf = FindLeaf(source);
        var targetLeaf = FindLeaf(target);
        if (sourceLeaf is null || targetLeaf is null)
            return;

        BeginTrailLayoutChange();

        CaptureLayoutSizes();

        _root = RemoveNode(_root, sourceLeaf);
        sourceLeaf.Weight = 1;
        var insertTarget = span ? PaneLayoutTree.ResolveSpanTarget(_root, targetLeaf, zone) : targetLeaf;
        _root = InsertRelative(_root, sourceLeaf, insertTarget, zone);

        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = MoveInTree(savedRoot, source, target, zone, span);

        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    private static PaneNode? MoveInTree(PaneNode root, PaneKind source, PaneKind target, DropZone zone, bool span = false)
        => PaneLayoutTree.MoveInTree(root, source, target, zone, span);

    private void PlaceWingPane(PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false) {
        if (dragged == target || FindLeaf(target) is null)
            return;

        BeginTrailLayoutChange();

        CaptureLayoutSizes();
        _enabledSessions.Add(dragged);   // タイルに出る＝有効

        _paneLayout.Place(dragged, target, center, zone, span);
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = PaneLayoutCoordinator.Place(savedRoot, dragged, target, center, zone, span);

        MarkLayoutDirty();
        RebuildPaneLayout();
        FocusPane(dragged);
        SaveActiveWorkspaceSnapshot();
    }

    private static PaneNode? RemoveNode(PaneNode? root, PaneNode node) => PaneLayoutTree.RemoveNode(root, node);

    private static PaneNode? InsertRelative(PaneNode? root, PaneNode node, PaneNode target, DropZone zone)
        => PaneLayoutTree.InsertRelative(root, node, target, zone);
}
