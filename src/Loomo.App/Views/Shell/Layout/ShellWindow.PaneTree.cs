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
        _paneLayout.Move(source, target, zone, span);
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = PaneLayoutTree.MoveInTree(savedRoot, source, target, zone, span);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }
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
}
