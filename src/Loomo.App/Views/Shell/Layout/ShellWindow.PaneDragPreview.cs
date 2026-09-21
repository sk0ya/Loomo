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
        DragVisuals.MoveGhost(e.GetPosition(DragGhostLayer));
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
            DragVisuals.HidePreview();
            Mouse.OverrideCursor = Cursors.No;
            return;
        }
        Mouse.OverrideCursor = Cursors.Hand;
        var relX = (pos.X - rect.X) / rect.Width;
        var relY = (pos.Y - rect.Y) / rect.Height;
        var selection = PaneDropPreviewPolicy.Select(relX, relY, allowCenter: true);
        var zone = selection.Zone;
        var center = selection.Center;
        _dragTarget = _stagePane;
        _dragZone = center ? null : zone;
        _dragCenter = center;
        DragVisuals.ShowPreview(
            rect, center ? rect : ToRect(PaneDropPreviewPolicy.ZoneBounds(ToLayoutRect(rect), zone)));
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
            DragVisuals.HidePreview();
            Mouse.OverrideCursor = Cursors.No;
            return;
        }
        Mouse.OverrideCursor = Cursors.Hand;
        var (kind, rect) = hit.Value;
        var relX = rect.Width > 0 ? (pos.X - rect.X) / rect.Width : 0.5;
        var relY = rect.Height > 0 ? (pos.Y - rect.Y) / rect.Height : 0.5;
        var selection = PaneDropPreviewPolicy.Select(relX, relY, _dragFromWing);
        var zone = selection.Zone;
        var center = selection.Center;
        var span = !center && selection.NearOuterEdge
            && TryGetSpanRect(kind, zone, out var spanRect)
            && PaneDropPreviewPolicy.SpanAddsBreadth(ToLayoutRect(spanRect), ToLayoutRect(rect), zone);
        var outlineRect = span ? spanRect : rect;
        var previewRect = center ? rect
            : ToRect(PaneDropPreviewPolicy.ZoneBounds(ToLayoutRect(span ? spanRect : rect), zone));
        _dragTarget = kind;
        _dragZone = zone;
        _dragCenter = center;
        _dragSpan = span;
        DragVisuals.ShowPreview(outlineRect, previewRect);
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
        DragVisuals.ShowPreview(zone, zone);
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
        DragVisuals.SetGhostDestination(_dragSource, toWing);
    }
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
    private void OnDragCanvasMouseUp(object sender, MouseButtonEventArgs e) {
        var commit = PaneDropPreviewPolicy.ResolveCommit(
            _dragSource, _dragTarget, _dragZone, _dragCenter, _dragSpan,
            _dragFromWing, _stageDrag, _dragToWing);
        EndPaneDrag();
        switch (commit.Kind) {
            case PaneDropCommitKind.Stage:
                HandleStageDrop(commit.Source, commit.Target, commit.Center, commit.Zone);
                break;
            case PaneDropCommitKind.Wing:
                MovePaneToWing(commit.Source);
                break;
            case PaneDropCommitKind.PlaceFromWing when commit.Target is { } placementTarget:
                PlaceWingPane(commit.Source, placementTarget, commit.Center, commit.Zone, commit.Span);
                break;
            case PaneDropCommitKind.MoveWithinLayout when commit.Target is { } moveTarget && commit.Zone is { } zone:
                MovePane(commit.Source, moveTarget, zone, commit.Span);
                break;
        }
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
        _enabledSessions.Add(source);
        _enabledSessions.Add(stage);
        _root = PaneStageDropPolicy.CreateRoot(source, stage, z);
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
        DragVisuals.ShowGhost(kind);
        Mouse.OverrideCursor = Cursors.Hand;   // 掴んでいる感じ（タイル外では UpdateDragPreview が No へ）
    }
    private void HideDragGhost() {
        _paneDragVisuals?.HideGhost();
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
        if (_paneDragVisuals is { } visuals && ReferenceEquals(Mouse.Captured, visuals.DragCanvas))
            Mouse.Capture(null);
        if (_paneDragVisuals is { } dragVisuals) {
            if (dragVisuals.DragCanvas is { } canvas)
                canvas.IsHitTestVisible = false;
            dragVisuals.HidePreview();
        }
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
    private static PaneLayoutRect ToLayoutRect(Rect rect)
        => new(rect.X, rect.Y, rect.Width, rect.Height);
    private static Rect ToRect(PaneLayoutRect rect)
        => new(rect.X, rect.Y, rect.Width, rect.Height);
}
