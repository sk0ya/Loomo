namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインドラッグ中のプレビュー描画・ドロップ確定・ゴースト追従・ゾーン計算 （オーバーレイ上のマウス追跡、挿入/入れ替えプレビュー、ヒットテスト、矩形/色ヘルパ）。 ドラッグ開始・捕捉・オーバーレイ生成は ShellWindow.PaneDrag.cs。</summary>
public partial class ShellWindow {
    private void OnDragCanvasMouseMove(object sender, MouseEventArgs e) {
        if (!_paneDragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed) {
            EndPaneDrag();
            return;
        }
        MoveDragGhost(e.GetPosition(DragGhostLayer));
        if (_stageDrag)
            UpdateStageDragPreview(e.GetPosition(PaneHost));
        else
            UpdateDragPreview(e.GetPosition(PaneHost));
    }
    private void UpdateStageDragPreview(Point pos) {
        var rect = StageRectInPaneHost();
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(pos)) {
            _dragTarget = null;
            _dragZone = null;
            _dragCenter = false;
            _dragPreview!.Visibility = Visibility.Collapsed;
            _dragTargetOutline!.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = Cursors.No;
            return;
        }
        Mouse.OverrideCursor = Cursors.Hand;
        var relX = (pos.X - rect.X) / rect.Width;
        var relY = (pos.Y - rect.Y) / rect.Height;
        var zone = NearestZone(relX, relY);
        var center = relX is > 0.34 and < 0.66 && relY is > 0.34 and < 0.66;
        _dragTarget = _stagePane;
        _dragZone = center ? null : zone;
        _dragCenter = center;
        PlaceOverlay(_dragTargetOutline!, rect);
        PlaceOverlay(_dragPreview!, center ? rect : ZoneRect(rect, zone));
        _dragTargetOutline!.Visibility = Visibility.Visible;
        _dragPreview!.Visibility = Visibility.Visible;
    }
    private Rect StageRectInPaneHost() {
        if (StageArea.ActualWidth <= 0 || StageArea.ActualHeight <= 0)
            return Rect.Empty;
        var topLeft = StageArea.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(StageArea.ActualWidth, StageArea.ActualHeight));
    }
    private void UpdateDragPreview(Point pos) {
        if (TryPreviewWingDrop(pos))
            return;
        var hit = HitTestCell(pos);
        if (hit is null) {
            _dragTarget = null;
            _dragZone = null;
            _dragCenter = false;
            _dragSpan = false;
            _dragPreview!.Visibility = Visibility.Collapsed;
            _dragTargetOutline!.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = Cursors.No;
            return;
        }
        Mouse.OverrideCursor = Cursors.Hand;
        var (kind, rect) = hit.Value;
        var relX = rect.Width > 0 ? (pos.X - rect.X) / rect.Width : 0.5;
        var relY = rect.Height > 0 ? (pos.Y - rect.Y) / rect.Height : 0.5;
        var zone = NearestZone(relX, relY);
        var center = _dragFromWing && relX is > 0.34 and < 0.66 && relY is > 0.34 and < 0.66;
        var span = !center && IsNearOuterEdge(relX, relY, zone)
            && TryGetSpanRect(kind, zone, out var spanRect) && SpanAddsBreadth(spanRect, rect, zone);
        var outlineRect = span ? spanRect : rect;
        var previewRect = center ? rect : ZoneRect(span ? spanRect : rect, zone);
        _dragTarget = kind;
        _dragZone = zone;
        _dragCenter = center;
        _dragSpan = span;
        PlaceOverlay(_dragTargetOutline!, outlineRect);
        PlaceOverlay(_dragPreview!, previewRect);
        _dragTargetOutline!.Visibility = Visibility.Visible;
        _dragPreview!.Visibility = Visibility.Visible;
    }
    /// <summary>袖の上なら「しまう」プレビュー（袖ぜんぶを塗る）を出して true。舞台に出ているペインを
    /// 掴んでいるときだけ受ける——袖のカードを掴んでいる場合は元の場所へ戻すだけなので、受け皿にしない。</summary>
    private bool TryPreviewWingDrop(Point pos) {
        var zone = WingDropZone.Compute(WingRectInPaneHost(), PaneHost.ActualHeight);
        // 受けない条件は MovePaneToWing と同じものをここにも置く——「しまう」表示を出しておいて
        // 離すと何も起きない、というズレを構造的に作らない（最後の1枚は BeginPaneDrag が
        // そもそも掴ませないので、いまは二重の守り）。
        if (_dragFromWing || VisibleLeafCount() <= 1 || !WingDropZone.Hits(zone, pos)) {
            SetDragToWing(false);
            return false;
        }
        SetDragToWing(true);
        _dragTarget = null;
        _dragZone = null;
        _dragCenter = false;
        _dragSpan = false;
        PlaceOverlay(_dragTargetOutline!, zone);
        PlaceOverlay(_dragPreview!, zone);
        _dragTargetOutline!.Visibility = Visibility.Visible;
        _dragPreview!.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.Hand;
        return true;
    }
    /// <summary>袖の矩形を <c>PaneHost</c> 座標で返す（袖が出ていなければ空）。プレビューの
    /// オーバーレイは <c>PaneHost</c> と同じ原点で袖の列まで伸びているので、この座標のまま置ける。</summary>
    private Rect WingRectInPaneHost() {
        if (WingHost is null || WingHost.Visibility != Visibility.Visible
            || WingHost.ActualWidth <= 0 || WingHost.ActualHeight <= 0)
            return Rect.Empty;
        var topLeft = WingHost.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(WingHost.ActualWidth, WingHost.ActualHeight));
    }
    /// <summary>掴んでいるチップの文言を行き先に合わせる（袖の上＝「→ 袖へ」）。</summary>
    private void SetDragToWing(bool toWing) {
        if (_dragToWing == toWing)
            return;
        _dragToWing = toWing;
        if (_dragGhost?.Child is TextBlock label)
            label.Text = toWing ? $"{PaneLabel(_dragSource)} → 袖へ" : PaneLabel(_dragSource);
    }
    private static bool IsNearOuterEdge(double relX, double relY, DropZone zone) => zone switch {
        DropZone.Left => relX < 0.2, DropZone.Right => relX > 0.8, DropZone.Above => relY < 0.2, _ => relY > 0.8,
    };
    private bool TryGetSpanRect(PaneKind targetKind, DropZone zone, out Rect rect) {
        rect = default;
        if (FindLeaf(targetKind) is not { } targetLeaf)
            return false;
        var node = PaneLayoutTree.ResolveSpanTarget(_root, targetLeaf, zone);
        if (ReferenceEquals(node, targetLeaf))
            return false; // 直交する祖先が無い＝単体ペインへの挿入と同じ
        var any = false;
        foreach (var leaf in AllLeaves(node)) {
            if (leaf.Hidden || !TryGetPaneRect(leaf.Kind, out var r))
                continue;
            rect = any ? Rect.Union(rect, r) : r;
            any = true;
        }
        return any;
    }
    private static bool SpanAddsBreadth(Rect span, Rect leaf, DropZone zone)
        => zone is DropZone.Above or DropZone.Below
            ? span.Width > leaf.Width + 1
            : span.Height > leaf.Height + 1;
    private static void PlaceOverlay(Border border, Rect r) {
        Canvas.SetLeft(border, r.X);
        Canvas.SetTop(border, r.Y);
        border.Width = r.Width;
        border.Height = r.Height;
    }
    private void OnDragCanvasMouseUp(object sender, MouseButtonEventArgs e) {
        var source = _dragSource;
        var target = _dragTarget;
        var zone = _dragZone;
        var center = _dragCenter;
        var span = _dragSpan;
        var fromWing = _dragFromWing;
        var stageDrag = _stageDrag;
        var toWing = _dragToWing;
        EndPaneDrag();
        if (stageDrag) {
            HandleStageDrop(source, target, center, zone);
            return;
        }
        if (toWing) {
            MovePaneToWing(source);
            return;
        }
        if (target is not { } t || t == source)
            return;
        if (fromWing)
            PlaceWingPane(source, t, center, zone, span);
        else if (zone is { } z)
            MovePane(source, t, z, span);
    }
    private void HandleStageDrop(PaneKind source, PaneKind? target, bool center, DropZone? zone) {
        if (target is not { } stage)   // 舞台の外でドロップ＝レイアウトは変えない
            return;
        if (center) {
            SetStagePane(source);
            FocusPane(source);
            return;
        }
        if (zone is not { } z || source == stage)
            return;
        var orientation = z is DropZone.Left or DropZone.Right ? SplitKind.Columns : SplitKind.Rows;
        var split = new PaneSplit { Orientation = orientation };
        var stageLeaf = new PaneLeaf { Kind = stage };
        var draggedLeaf = new PaneLeaf { Kind = source };
        if (z is DropZone.Left or DropZone.Above) {
            split.Children.Add(draggedLeaf);
            split.Children.Add(stageLeaf);
        } else {
            split.Children.Add(stageLeaf);
            split.Children.Add(draggedLeaf);
        }
        _enabledSessions.Add(source);
        _enabledSessions.Add(stage);
        _root = split;
        ExitStageMode();        // 新しい _root でタイルを組み直す（→ レイアウトモード）
        MarkLayoutDirty();      // ステージ解除後なので「未保存」印が立つ
        FocusPane(source);
        SaveActiveWorkspaceSnapshot();
    }
    private void OnDragCanvasLostCapture(object sender, MouseEventArgs e) {
        if (!_paneDragging)
            return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) {
            Dispatcher.BeginInvoke(new Action(BeginDragCapture), System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        EndPaneDrag();
    }
    private void ShowDragGhost(PaneKind kind) {
        if (_dragGhost is null) {
            _dragGhost = new Border {
                Background = MakeTranslucent((Brush)FindResource("Accent"), 0.9), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 5, 10, 5), IsHitTestVisible = false, Effect = new System.Windows.Media.Effects.DropShadowEffect {
                    BlurRadius = 8, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black
                }, Child = new TextBlock { Foreground = (Brush)FindResource("AccentFg"), FontSize = UiFontManager.Scaled(12), FontWeight = FontWeights.SemiBold }
            };
            DragGhostLayer.Children.Add(_dragGhost);
        }
        ((TextBlock)_dragGhost.Child).Text = PaneLabel(kind);
        _dragGhost.Visibility = Visibility.Visible;
        DragGhostLayer.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.Hand;   // 掴んでいる感じ（タイル外では UpdateDragPreview が No へ）
    }
    private void MoveDragGhost(Point pos) {
        if (_dragGhost is null)
            return;
        Canvas.SetLeft(_dragGhost, pos.X + 14);
        Canvas.SetTop(_dragGhost, pos.Y + 16);
    }
    private void HideDragGhost() {
        if (_dragGhost is not null)
            _dragGhost.Visibility = Visibility.Collapsed;
        DragGhostLayer.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
    }
    private void EndPaneDrag() {
        _paneDragging = false;
        _dragFromWing = false;
        _stageDrag = false;
        _dragToWing = false;
        _dragCenter = false;
        _dragSpan = false;
        HideDragGhost();
        if (ReferenceEquals(Mouse.Captured, _dragCanvas))
            Mouse.Capture(null);
        if (_dragCanvas is not null)
            _dragCanvas.IsHitTestVisible = false;
        if (_dragPreview is not null)
            _dragPreview.Visibility = Visibility.Collapsed;
        if (_dragTargetOutline is not null)
            _dragTargetOutline.Visibility = Visibility.Collapsed;
    }
    private (PaneKind Kind, Rect Rect)? HitTestCell(Point pos) {
        foreach (var leaf in AllLeaves()) {
            if (leaf.Hidden)
                continue;
            if (TryGetPaneRect(leaf.Kind, out var rect) && rect.Contains(pos))
                return (leaf.Kind, rect);
        }
        return null;
    }
    private static DropZone NearestZone(double relX, double relY) {
        var dLeft = relX;
        var dRight = 1 - relX;
        var dTop = relY;
        var dBottom = 1 - relY;
        var min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
        if (min == dLeft) return DropZone.Left;
        if (min == dRight) return DropZone.Right;
        if (min == dTop) return DropZone.Above;
        return DropZone.Below;
    }
    private static Rect ZoneRect(Rect r, DropZone zone) => zone switch {
        DropZone.Left => new Rect(r.X, r.Y, r.Width / 2, r.Height), DropZone.Right => new Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height), DropZone.Above => new Rect(r.X, r.Y, r.Width, r.Height / 2), _ => new Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2),
    };
    private static Brush MakeTranslucent(Brush source, double opacity) {
        if (source is SolidColorBrush solid) {
            var c = solid.Color;
            return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));
        }
        var clone = source.Clone();
        clone.Opacity = opacity;
        return clone;
    }
}
