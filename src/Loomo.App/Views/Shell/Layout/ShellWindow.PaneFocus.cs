namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: フォーカス追跡と方向移動（Ctrl+W h/j/k/l）。フォーカス領域の記録、隣接領域の探索、 ビューポート/サイドバー/ペインへのフォーカス適用、ペイン/サイドバー矩形の取得。 キー入口・リサイズモードは ShellWindow.PaneNavigation.cs。</summary>
public partial class ShellWindow {
    private readonly Dictionary<PaneKind, WeakReference<IInputElement>> _lastPaneFocus = new();
    private WeakReference<IInputElement>? _lastSidebarFocus;

    /// <summary>最後に「ペイン／サイドバーの内部」が持っていたキーボードフォーカス（位置と要素の対）。
    /// アクティビティバーのボタンや本体外のウィンドウは内部ではないので更新しない。設定ウィンドウを
    /// 挟んだあとの戻り先の起点になる（設計書 §31.8）。</summary>
    private (PaneFocusTarget Target, WeakReference<IInputElement> Element)? _lastInnerFocus;

    private void OnWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
        _keyboard?.OnExternalFocusChange(suppressModeExit: _suppressResizeExit);
        if (e.NewFocus is not DependencyObject d)
            return;
        if (PaneFocusElementResolver.FindPaneOf(d, _paneElements) is { } kind) {
            if (PaneFocusNavigationPolicy.ShouldRememberPaneFocus(
                    kind, e.NewFocus is System.Windows.Controls.Primitives.ButtonBase))
                _lastPaneFocus[kind] = new WeakReference<IInputElement>(e.NewFocus);
            if (ViewsFor(kind) is { } views && views.SetFocusedFromElement(d) is { } viewId)
                _focusedRegion = PaneFocusTarget.Viewport(kind, viewId);
            else
                _focusedRegion = PaneFocusTarget.Of(kind);
            _lastInnerFocus = (_focusedRegion.Value, new WeakReference<IInputElement>(e.NewFocus));
            RecordTrailPane(kind);
        } else if (IsWithin(d, SidebarContainer)) {
            _focusedRegion = PaneFocusTarget.Sidebar;
            _lastSidebarFocus = new WeakReference<IInputElement>(e.NewFocus);
            _lastInnerFocus = (PaneFocusTarget.Sidebar, _lastSidebarFocus);
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
                ApplyFocusTarget(PaneFocusTarget.Viewport(viewportPane, decision.ViewportId));
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
        => _paneElements.ContainsKey(kind) && IsPaneMaterialized(kind);
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
        => FocusReturnElement.IsWithin(element, ancestor);
    private void OnWindowDeactivated(object? sender, EventArgs e)
        => _keyboard?.Reset();
    private void FocusPaneInDirection(DropZone direction) {
        if (_stageActive && _focusedRegion?.Pane is { } stageFocused
            && ViewsFor(stageFocused) is { LeafCount: > 1 } stageViews) {
            if (stageViews.FocusInDirection(direction, PaneHost)
                && stageViews.FocusedViewportId is { } viewportId) {
                _focusedRegion = PaneFocusTarget.Viewport(stageFocused, viewportId);
                SyncActiveFromViewport(stageFocused);
                return;
            }
        }
        if (_stageActive) {
            CycleStage(PaneFocusNavigationPolicy.StageCycleDirection(direction));
            return;
        }
        if (PaneFocusNavigationPolicy.FindNeighbor(FocusTargets().ToList(), _focusedRegion, direction) is { } target)
            ApplyFocusTarget(target);
    }
    private IEnumerable<PaneFocusCandidate> FocusTargets() {
        foreach (var leaf in AllLeaves()) {
            if (leaf.Hidden)
                continue;
            if (ViewsFor(leaf.Kind) is { LeafCount: > 1 } views) {
                foreach (var (id, rect) in views.ViewportRects(PaneHost))
                    yield return PaneFocusElementResolver.CreateCandidate(
                        PaneFocusTarget.Viewport(leaf.Kind, id), rect);
            } else if (_paneElements.TryGetValue(leaf.Kind, out var element)
                && PaneFocusElementResolver.TryGetVisibleBounds(element, PaneHost, out var bounds)) {
                yield return new PaneFocusCandidate(PaneFocusTarget.Of(leaf.Kind), bounds);
            }
        }
        if (_vm.IsSidebarVisible && SidebarContainer.IsVisible
            && PaneFocusElementResolver.TryGetVisibleBounds(SidebarContainer, PaneHost, out var sidebarBounds))
            yield return new PaneFocusCandidate(PaneFocusTarget.Sidebar, sidebarBounds);
    }
    private PaneSplitView? ViewsFor(PaneKind kind) => kind switch {
        PaneKind.Editor => _editorViews, PaneKind.Terminal => _terminalViews, _ => null
    };
    private void ApplyFocusTarget(PaneFocusTarget target) {
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
        PaneFocusElementResolver.FocusSidebar(SidebarContainer, _lastSidebarFocus, view => {
            // Explorer はセクションの中にツリーがあるため、可視の子を直接探索する。
            if (view is FolderTreeView tree)
                tree.FocusTree();
            else if (ReferenceEquals(view, ExplorerSection))
                SidebarFolderTree.FocusTree();
            else
                PaneFocusElementResolver.FocusFirstFocusable(view);
        }, () => _focusedRegion = PaneFocusTarget.Sidebar);
    }
    private void FocusPane(PaneKind kind) {
        // ドックに置かない面（Diff 等）は出せないので、現在地も動かさない。動かすと軌跡の点や
        // パレットを閉じた後の「元へ戻す」が、画面に居ない面へフォーカスを当てて何も起きない。
        if (_dockActive && !DockLayoutCoordinator.IsDockable(kind))
            return;
        if (_stageActive && kind != _stagePane)
            SetStagePane(kind);
        else if (_dockActive && !_dockMode.IsOpen(kind))
            EnsureDockPaneShown(kind);   // 出ていない面へフォーカスが来たら、その領域に出して見せる
        _focusedRegion = PaneFocusTarget.Of(kind);
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
                // ペインの器ではなくコミット一覧へ落とす（そのまま j/k・Enter が効く）。
                GitSessionHost.FocusCommitList();
                break;
            case PaneKind.Diff:
                DiffSessionHost.Focus();
                break;
            case PaneKind.Trace:
                TraceSessionHost.Focus();
                break;
            case PaneKind.Debug:
                PaneFocusElementResolver.FocusFirstFocusable(DebugPane);
                break;
            case PaneKind.TsIde:
                PaneFocusElementResolver.FocusFirstFocusable(TsIdePane);
                break;
            case PaneKind.Search:
                SearchPaneHost.FocusQuery();
                break;
            case PaneKind.Files:
                FilesPaneHost.FocusList();
                break;
        }
        RecordTrailPane(kind);
    }

    private static bool TryRestoreFocus(WeakReference<IInputElement>? reference, DependencyObject owner)
        => PaneFocusElementResolver.TryRestoreFocus(reference, owner);

    private bool TryGetPaneRect(PaneKind kind, out Rect rect)
    {
        rect = default;
        if (!_paneElements.TryGetValue(kind, out var element)
            || !PaneFocusElementResolver.TryGetVisibleBounds(element, PaneHost, out var bounds))
            return false;
        rect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        return true;
    }

    /// <summary>ワークスペース復元の最後に、見えている場所と内部の現在地を同じペインへ揃える。</summary>
    private void RestoreActivePane(WorkspaceSnapshot workspace) {
        var target = PaneFocusNavigationPolicy.ResolveRestorePane(
            _stageActive, _stagePane, workspace.ActivePane,
            pane => _paneElements.ContainsKey(pane) && (_stageActive || IsPaneMaterialized(pane)));
        if (target is not { } pane)
            return;
        if (_overviewActive) {
            _focusedRegion = PaneFocusTarget.Of(pane);
            return;
        }
        FocusPane(pane);
    }
}
