namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインのドラッグ＆ドロップ操作（タイトルバーからの掴み・袖/舞台からのドラッグ・ オーバーレイ上のプレビュー描画・ドロップ確定）。レイアウトツリーの構築は <c>ShellWindow.PaneLayout.cs</c>。</summary>
public partial class ShellWindow {
    private PaneDragVisualPresenter? _paneDragVisuals;
    private PaneDragVisualPresenter DragVisuals
        => _paneDragVisuals ??= new PaneDragVisualPresenter(
            PaneDragOverlay, DragGhostLayer, this, PaneLabel);

    private void OnPaneTitleMouseDown(object sender, MouseButtonEventArgs e) {
        if (_stageActive)
            return;
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        // ドックでは中央も1枚なので、どの面もタイルの並べ替え対象ではない。
        // 代わりに「割り当てを変えるドラッグ」を仕込む（行き先は右帯の3区画）。
        if (_dockActive) {
            if (!IsWithinButton(e.OriginalSource) && ResolvePaneTabId(e.OriginalSource) is null)
                DockPaneDrag.Arm((UIElement)sender, kind, e.GetPosition(null));
            return;
        }
        if (e.ClickCount == 2) {
            if (IsWithinButton(e.OriginalSource))
                return;
            ToggleZoomFor(kind);
            e.Handled = true;
            return;
        }
        if (ResolvePaneTabId(e.OriginalSource) is not null) {
            _paneDragArmed = false;
            return;
        }
        _paneDragStart = e.GetPosition(null);
        _paneDragArmed = true;
    }
    private void OnPaneTitleMouseMove(object sender, MouseEventArgs e) {
        if (_stageActive)
            return;
        // ドックのヘッダーは「割り当てを変えるドラッグ」（DockPaneDrag.Arm で仕込み済み・しきい値の
        // 監視はウィンドウ側）なので、タイルの並べ替えには進ませない。
        if (_dockActive)
            return;
        if (_paneDragging || !_paneDragArmed)
            return;
        if (e.LeftButton != MouseButtonState.Pressed) {
            DisarmTitleDrag();
            return;
        }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _paneDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _paneDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind)) {
            DisarmTitleDrag();
            BeginPaneDrag(kind);
        }
    }
    private void OnPaneTitleMouseUp(object sender, MouseButtonEventArgs e) {
        DisarmTitleDrag();
    }
    private void DisarmTitleDrag() {
        _paneDragArmed = false;
        if (_dragHandle is not null) {
            if (ReferenceEquals(Mouse.Captured, _dragHandle))
                _dragHandle.ReleaseMouseCapture();
            _dragHandle = null;
        }
    }
    private static bool IsWithinButton(object? source) {
        var current = source as DependencyObject;
        while (current is not null) {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    private void BeginPaneDrag(PaneKind source) {
        if (_zoomedPane is not null)
            return; // ズーム中は移動先が1枚しか見えないので並べ替えしない
        if (VisibleLeafCount() <= 1)
            return; // 1枚だけなら移動先がない
        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = false;
        _dragCenter = false;
        _dragSpan = false;
        _paneDragging = true;
        DragVisuals.HidePreview();
        ShowDragGhost(source);
        DragVisuals.MoveGhost(Mouse.GetPosition(DragGhostLayer));
        BeginDragCapture();
    }
    private void BeginWingDrag(PaneKind source) {
        if (_stageActive || VisibleLeafCount() < 1)
            return;
        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = true;
        _dragCenter = false;
        _dragSpan = false;
        _paneDragging = true;
        DragVisuals.HidePreview();
        ShowDragGhost(source);
        DragVisuals.MoveGhost(Mouse.GetPosition(DragGhostLayer));
        BeginDragCapture();
    }
    private void BeginStageDrag(PaneKind source) {
        if (!_stageActive || _overviewActive)
            return;
        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = true;
        _dragCenter = false;
        _dragSpan = false;
        _stageDrag = true;
        _paneDragging = true;
        DragVisuals.HidePreview();
        ShowDragGhost(source);
        DragVisuals.MoveGhost(Mouse.GetPosition(DragGhostLayer));
        BeginDragCapture();
    }
    private void BeginDragCapture() {
        DragVisuals.DragCanvas!.IsHitTestVisible = true;   // 素通し→掴める状態へ（EndPaneDrag で false へ戻す）
        if (TryCaptureDragCanvas())
            return;
        var attempts = 0;
        void Retry() {
            if (!_paneDragging || Mouse.LeftButton != MouseButtonState.Pressed)
                return;                                       // ドラッグ終了／ボタンが離れた＝もう不要
            if (TryCaptureDragCanvas() || ++attempts >= 5)
                return;
            Dispatcher.BeginInvoke(new Action(Retry), System.Windows.Threading.DispatcherPriority.Input);
        }
        Dispatcher.BeginInvoke(new Action(Retry), System.Windows.Threading.DispatcherPriority.Input);
    }
    private bool TryCaptureDragCanvas() {
        var canvas = DragVisuals.DragCanvas;
        return canvas is not null && (ReferenceEquals(Mouse.Captured, canvas)
            || Mouse.Capture(canvas, CaptureMode.SubTree));
    }
    private void EnsureDragOverlay() {
        DragVisuals.EnsureOverlay(canvas => {
            canvas.MouseMove += OnDragCanvasMouseMove;
            canvas.MouseLeftButtonUp += OnDragCanvasMouseUp;
            canvas.LostMouseCapture += OnDragCanvasLostCapture;
        });
    }
}
