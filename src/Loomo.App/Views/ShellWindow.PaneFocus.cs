namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: フォーカス追跡と方向移動（Ctrl+W h/j/k/l）。フォーカス領域の記録、隣接領域の探索、 ビューポート/サイドバー/ペインへのフォーカス適用、ペイン/サイドバー矩形の取得。 キー入口・リサイズモードは ShellWindow.PaneNavigation.cs。</summary>
public partial class ShellWindow {
    private readonly Dictionary<PaneKind, WeakReference<IInputElement>> _lastPaneFocus = new();
    private WeakReference<IInputElement>? _lastSidebarFocus;

    /// <summary>最後に「ペイン／サイドバーの内部」が持っていたキーボードフォーカス（位置と要素の対）。
    /// アクティビティバーのボタンや本体外のウィンドウは内部ではないので更新しない。設定ウィンドウを
    /// 挟んだあとの戻り先の起点になる（設計書 §31.8）。</summary>
    private (FocusTarget Target, WeakReference<IInputElement> Element)? _lastInnerFocus;

    private void OnWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
        _keyboard?.OnExternalFocusChange(suppressModeExit: _suppressResizeExit);
        if (e.NewFocus is not DependencyObject d)
            return;
        if (FindPaneOf(d) is { } kind) {
            if (kind is PaneKind.Debug or PaneKind.TsIde or PaneKind.Ai or PaneKind.Git or PaneKind.Diff or PaneKind.Trace
                && e.NewFocus is not System.Windows.Controls.Primitives.ButtonBase)
                _lastPaneFocus[kind] = new WeakReference<IInputElement>(e.NewFocus);
            if (ViewsFor(kind) is { } views && views.SetFocusedFromElement(d) is { } viewId)
                _focusedRegion = FocusTarget.Viewport(kind, viewId);
            else
                _focusedRegion = FocusTarget.Of(kind);
            _lastInnerFocus = (_focusedRegion.Value, new WeakReference<IInputElement>(e.NewFocus));
            RecordTrailPane(kind);
        } else if (IsWithin(d, SidebarContainer)) {
            _focusedRegion = FocusTarget.Sidebar;
            _lastSidebarFocus = new WeakReference<IInputElement>(e.NewFocus);
            _lastInnerFocus = (FocusTarget.Sidebar, _lastSidebarFocus);
        }
    }

    // ===== 設定ウィンドウ（本体の外で入力を受ける面）を挟んだときのフォーカス復帰（設計書 §31.8） =====

    private FocusReturnOrigin? _focusReturnOrigin;
    private WeakReference<IInputElement>? _focusReturnElement;

    /// <summary>設定ウィンドウを開く直前の「最後の内部フォーカス」を控える。開く操作自体が
    /// アクティビティバーのボタンへフォーカスを移していることがあるので、現在のフォーカスではなく
    /// <see cref="_lastInnerFocus"/>（ペイン／サイドバー内部に最後にあった位置）を使う。
    /// 開いている間の内部フォーカス変化では更新しない——閉じる過程で本体が再アクティブ化されるときの
    /// 横取りまで拾ってしまい、直そうとしている状態そのものを起点にしてしまうため。</summary>
    private void CaptureFocusReturnOrigin() {
        if (_lastInnerFocus is not { } last) {
            _focusReturnOrigin = null;
            _focusReturnElement = null;
            return;
        }
        _focusReturnOrigin = new FocusReturnOrigin(last.Target.Pane, last.Target.ViewportId);
        _focusReturnElement = last.Element;
    }

    /// <summary>設定ウィンドウを閉じたあと、控えておいた場所へフォーカスを戻す。
    /// 本体が再アクティブ化される過程で WebView2（ブラウザペイン）が非同期にフォーカスを取りにいくため、
    /// その後で実行されるよう <see cref="DispatcherPriority.Background"/> へ回してから適用する。</summary>
    private void RestoreFocusReturnOrigin() {
        var origin = _focusReturnOrigin;
        var element = _focusReturnElement;
        _focusReturnOrigin = null;
        _focusReturnElement = null;
        if (origin is null || Dispatcher.HasShutdownStarted || !IsLoaded)
            return;     // 本体ごと終了する経路（Owner が閉じて一緒に閉じた）では戻す先が無い
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => ApplyFocusReturn(origin.Value, element)));
    }

    /// <summary>戻り先を <see cref="FocusReturnPolicy"/> に決めさせて適用する（判断は純ロジック側）。</summary>
    private void ApplyFocusReturn(FocusReturnOrigin origin, WeakReference<IInputElement>? element) {
        var target = FocusReturnElement.ResolveLive(element, this);
        var paneAvailable = origin.Pane is { } pane && IsPaneFocusableNow(pane);
        var viewportAlive = origin.Pane is { } vpPane && origin.ViewportId != default
            && ViewsFor(vpPane)?.HasViewport(origin.ViewportId) == true;
        var sidebarVisible = _vm.IsSidebarVisible && SidebarContainer.IsVisible;

        var decision = FocusReturnPolicy.Decide(origin, target is not null, paneAvailable, viewportAlive, sidebarVisible);
        if (decision.Kind == FocusReturnKind.Element) {
            if (target!.Focus()) {
                if (origin.Pane is { } focusedPane)
                    SyncActiveFromViewport(focusedPane);
                return;
            }
            // 要素は残っていたがフォーカスを受け取らなかった。要素なしとして決め直す。
            decision = FocusReturnPolicy.Decide(origin, false, paneAvailable, viewportAlive, sidebarVisible);
        }
        switch (decision.Kind) {
            case FocusReturnKind.Viewport when decision.Pane is { } viewportPane:
                ApplyFocusTarget(FocusTarget.Viewport(viewportPane, decision.ViewportId));
                break;
            case FocusReturnKind.Pane when decision.Pane is { } panePane:
                FocusPane(panePane);
                break;
            case FocusReturnKind.Sidebar:
                FocusSidebar();
                break;
        }
    }

    /// <summary>そのペインへ今フォーカスを戻せるか（舞台中は表に出し直せるので可）。</summary>
    private bool IsPaneFocusableNow(PaneKind kind)
        => _paneElements.ContainsKey(kind) && (_stageActive || IsPaneVisible(kind));
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
        => FocusReturnElement.IsWithin(element, ancestor);
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
        if (TryRestoreFocus(_lastSidebarFocus, SidebarContainer))
            return;
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
        if (_paneElements.TryGetValue(kind, out var pane) &&
            _lastPaneFocus.TryGetValue(kind, out var previous) && TryRestoreFocus(previous, pane))
        {
            SyncActiveFromViewport(kind);
            RecordTrailPane(kind);
            return;
        }
        switch (kind) {
            case PaneKind.Terminal:
                if (_terminalViews is { } tv) tv.FocusFocused();
                else _activeTerminalTab?.View.FocusTerminal();
                SyncActiveFromViewport(kind);
                break;
            case PaneKind.Editor:
                if (_editorViews is { } ev) ev.FocusFocused();
                else _activeEditorTab?.Control.Focus();
                // ステージ再構築では表示中のビューポートと _activeEditorTab がずれることがある。
                // 共有ステータスバーや EditorSupport も、実際にフォーカスしたタブへ揃える。
                SyncActiveFromViewport(kind);
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
                SearchPaneHost.FocusQuery();
                break;
        }
        RecordTrailPane(kind);
    }

    private static bool TryRestoreFocus(WeakReference<IInputElement>? reference, DependencyObject owner)
    {
        if (reference?.TryGetTarget(out var target) != true || target is not DependencyObject d ||
            target is not UIElement { IsVisible: true, IsEnabled: true })
            return false;
        if (!IsWithin(d, owner)) return false;
        return target.Focus();
    }

    /// <summary>ワークスペース復元の最後に、見えている場所と内部の現在地を同じペインへ揃える。</summary>
    private void RestoreActivePane(WorkspaceSnapshot workspace) {
        var target = _stageActive ? _stagePane : workspace.ActivePane;
        if (target is not { } pane || !_paneElements.ContainsKey(pane))
            return;
        if (!_stageActive && !IsPaneVisible(pane))
            return;
        if (_overviewActive) {
            _focusedRegion = FocusTarget.Of(pane);
            return;
        }
        FocusPane(pane);
    }
}
