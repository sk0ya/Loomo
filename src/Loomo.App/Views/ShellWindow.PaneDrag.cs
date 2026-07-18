
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインのドラッグ＆ドロップ操作（タイトルバーからの掴み・袖/舞台からのドラッグ・ オーバーレイ上のプレビュー描画・ドロップ確定）。レイアウトツリーの構築は <c>ShellWindow.PaneLayout.cs</c>。</summary>
public partial class ShellWindow {
    private void OnPaneTitleMouseDown(object sender, MouseButtonEventArgs e) {
        if (_stageActive)
            return;
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;

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

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

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

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

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

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

        BeginDragCapture();
    }

    private void BeginDragCapture() {
        _dragCanvas!.IsHitTestVisible = true;   // 素通し→掴める状態へ（EndPaneDrag で false へ戻す）
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

    private bool TryCaptureDragCanvas()
        => ReferenceEquals(Mouse.Captured, _dragCanvas)
           || Mouse.Capture(_dragCanvas, CaptureMode.SubTree);

    private void EnsureDragOverlay() {
        if (_dragCanvas is not null)
            return;

        var accent = (Brush)FindResource("Accent");
        _dragTargetOutline = new Border {
            BorderBrush = accent, BorderThickness = new Thickness(1), Background = MakeTranslucent(accent, 0.10), Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _dragPreview = new Border {
            BorderBrush = accent, BorderThickness = new Thickness(2), Background = MakeTranslucent(accent, 0.35), CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _dragCanvas = new Canvas {
            Background = Brushes.Transparent, ClipToBounds = true, IsHitTestVisible = false, };
        _dragCanvas.Children.Add(_dragTargetOutline);
        _dragCanvas.Children.Add(_dragPreview);
        _dragCanvas.MouseMove += OnDragCanvasMouseMove;
        _dragCanvas.MouseLeftButtonUp += OnDragCanvasMouseUp;
        _dragCanvas.LostMouseCapture += OnDragCanvasLostCapture;
        PaneDragOverlay.Children.Add(_dragCanvas);
        PaneDragOverlay.Visibility = Visibility.Visible;
        PaneDragOverlay.UpdateLayout();
    }
}
