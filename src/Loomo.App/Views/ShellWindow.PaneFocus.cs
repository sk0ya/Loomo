namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: フォーカス追跡と方向移動（Ctrl+W h/j/k/l）。フォーカス領域の記録、隣接領域の探索、 ビューポート/サイドバー/ペインへのフォーカス適用、ペイン/サイドバー矩形の取得。 キー入口・リサイズモードは ShellWindow.PaneNavigation.cs。</summary>
public partial class ShellWindow {
    private void OnWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
        _keyboard?.OnExternalFocusChange(suppressModeExit: _suppressResizeExit);
        if (e.NewFocus is not DependencyObject d)
            return;
        if (FindPaneOf(d) is { } kind) {
            if (ViewsFor(kind) is { } views && views.SetFocusedFromElement(d) is { } viewId)
                _focusedRegion = FocusTarget.Viewport(kind, viewId);
            else
                _focusedRegion = FocusTarget.Of(kind);
            RecordTrailPane(kind);
        } else if (IsWithin(d, SidebarContainer))
            _focusedRegion = FocusTarget.Sidebar;
    }
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor) {
        for (var current = element; current is not null; current = GetAnyParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }
    private void OnWindowDeactivated(object? sender, EventArgs e)
        => _keyboard?.Reset();
    private PaneKind? FindPaneOf(DependencyObject element) {
        for (var current = element; current is not null; current = GetAnyParent(current)) {
            foreach (var (kind, paneElement) in _paneElements)
                if (ReferenceEquals(paneElement, current))
                    return kind;
        }
        return null;
    }
    private static DependencyObject? GetAnyParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);
    private void FocusPaneInDirection(DropZone direction) {
        if (_stageActive && _focusedRegion?.Pane is { } stageFocused
            && ViewsFor(stageFocused) is { LeafCount: > 1 } stageViews) {
            if (stageViews.FocusInDirection(direction, PaneHost)
                && stageViews.FocusedViewportId is { } viewportId) {
                _focusedRegion = FocusTarget.Viewport(stageFocused, viewportId);
                SyncActiveFromViewport(stageFocused);
                return;
            }
        }
        if (_stageActive) {
            CycleStage(StageCycleDirection(direction));
            return;
        }
        var targets = FocusTargets().ToList();
        if (targets.Count == 0)
            return;
        var originIndex = _focusedRegion is { } region
            ? targets.FindIndex(t => t.Target == region)
            : -1;
        if (originIndex < 0)
            originIndex = 0;
        var (originTarget, from) = targets[originIndex];
        var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        FocusTarget? best = null;
        var bestScore = double.MaxValue;
        foreach (var (target, r) in targets) {
            if (target == originTarget)
                continue;
            const double tolerance = 1.0;
            var inDirection = direction switch {
                DropZone.Left => r.X + r.Width <= from.X + tolerance, DropZone.Right => r.X >= from.X + from.Width - tolerance, DropZone.Above => r.Y + r.Height <= from.Y + tolerance, _ => r.Y >= from.Y + from.Height - tolerance, };
            if (!inDirection)
                continue;
            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
            var score = axis + perpendicular * 2;
            if (score < bestScore) {
                bestScore = score;
                best = target;
            }
        }
        if (best is { } target2)
            ApplyFocusTarget(target2);
    }
    private static int StageCycleDirection(DropZone direction)
        => direction is DropZone.Below or DropZone.Right ? 1 : -1;
    private IEnumerable<(FocusTarget Target, Rect Rect)> FocusTargets() {
        foreach (var leaf in AllLeaves()) {
            if (leaf.Hidden)
                continue;
            if (ViewsFor(leaf.Kind) is { LeafCount: > 1 } views) {
                foreach (var (id, rect) in views.ViewportRects(PaneHost))
                    yield return (FocusTarget.Viewport(leaf.Kind, id), rect);
            } else if (TryGetPaneRect(leaf.Kind, out var rect)) {
                yield return (FocusTarget.Of(leaf.Kind), rect);
            }
        }
        if (TryGetSidebarRect(out var sidebarRect))
            yield return (FocusTarget.Sidebar, sidebarRect);
    }
    private PaneSplitView? ViewsFor(PaneKind kind) => kind switch {
        PaneKind.Editor => _editorViews, PaneKind.Terminal => _terminalViews, _ => null
    };
    private bool TryGetSidebarRect(out Rect rect) {
        rect = default;
        if (!_vm.IsSidebarVisible || !SidebarContainer.IsVisible
            || SidebarContainer.ActualWidth <= 0 || SidebarContainer.ActualHeight <= 0)
            return false;
        var topLeft = SidebarContainer.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(SidebarContainer.ActualWidth, SidebarContainer.ActualHeight));
        return true;
    }
    private void ApplyFocusTarget(FocusTarget target) {
        if (target.IsSidebar) {
            FocusSidebar();
            return;
        }
        var kind = target.Pane!.Value;
        if (target.ViewportId != default && ViewsFor(kind) is { } views) {
            views.FocusViewport(target.ViewportId);
            _focusedRegion = target;
            SyncActiveFromViewport(kind);
        } else {
            FocusPane(kind);
        }
    }
    private void SyncActiveFromViewport(PaneKind kind) {
        if (kind == PaneKind.Editor && _editorViews?.FocusedTabId is { } eid
            && _editorTabs.FirstOrDefault(t => t.Id == eid) is { } et)
            SetActiveEditorTab(et);
        else if (kind == PaneKind.Terminal && _terminalViews?.FocusedTabId is { } tid
            && _terminalTabs.FirstOrDefault(t => t.Id == tid) is { } tt)
            SetActiveTerminalTab(tt);
    }
    private void FocusSidebar() {
        if (!_vm.IsSidebarVisible)
            return;
        var view = SidebarContainer.Children.OfType<UIElement>()
            .FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (view is null)
            return;
        _focusedRegion = FocusTarget.Sidebar;
        if (view is FolderTreeView tree)
            tree.FocusTree();           // Explorer は中身のツリーへ直接フォーカス（先頭未選択なら選ぶ）
        else
            FocusFirstFocusable(view);  // 他パネルは最初のフォーカス可能要素へ
    }
    private static bool FocusFirstFocusable(DependencyObject root) {
        if (root is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element) {
            element.Focus();
            return true;
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FocusFirstFocusable(VisualTreeHelper.GetChild(root, i)))
                return true;
        return false;
    }
    private bool TryGetPaneRect(PaneKind kind, out Rect rect) {
        rect = default;
        if (!_paneElements.TryGetValue(kind, out var element)
            || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;
        var topLeft = element.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return true;
    }
    private void FocusPane(PaneKind kind) {
        if (_stageActive && kind != _stagePane)
            SetStagePane(kind);
        _focusedRegion = FocusTarget.Of(kind);
        switch (kind) {
            case PaneKind.Terminal:
                if (_terminalViews is { } tv) tv.FocusFocused();
                else _activeTerminalTab?.View.FocusTerminal();
                break;
            case PaneKind.Editor:
                if (_editorViews is { } ev) ev.FocusFocused();
                else _activeEditorTab?.Control.Focus();
                break;
            case PaneKind.EditorSupport:
                _editorSupport.WebView.View?.Focus();
                break;
            case PaneKind.Browser:
                _activeBrowserTab?.View.Focus();
                break;
            case PaneKind.Ai:
                AiBarHost.FocusInput();
                break;
            case PaneKind.Git:
                GitSessionHost.Focus();
                break;
            case PaneKind.Diff:
                DiffSessionHost.Focus();
                break;
            case PaneKind.Trace:
                TraceSessionHost.Focus();
                break;
            case PaneKind.Debug:
                FocusFirstFocusable(DebugPane);
                break;
            case PaneKind.TsIde:
                FocusFirstFocusable(TsIdePane);
                break;
            case PaneKind.Search:
                SearchPaneHost.Focus();
                break;
        }
        RecordTrailPane(kind);
    }
}
