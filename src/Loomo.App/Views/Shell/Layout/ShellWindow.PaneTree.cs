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
    /// <summary>ペインの配置全体を回す・裏返す・縦横を入れ替える・中身を巡回させる（§24.84）。タイル（分割・ドックの中央）に効く。
    /// 集中（舞台＋袖）の間は配置の木が見えていないので何もしない。</summary>
    private void TransformPaneLayout(Func<PaneNode, PaneNode> transform) {
        if (_stageActive) {
            ToastService.Info("ペインの配置は分割表示のときに変えられます");
            return;
        }
        if (_root is null)
            return;
        BeginTrailLayoutChange();
        CaptureLayoutSizes();
        _root = PaneLayoutTree.Normalize(transform(_root));
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = PaneLayoutTree.Normalize(transform(savedRoot));
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
