
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ソロモード（舞台＋袖）。1ペインを全面の「舞台」に立て、残りのペインは
/// 右端の「袖」でペインを VisualBrush として縮小表示する。袖カードは実コントロールを
/// 子に持たず、元の表示を描くだけなので、袖表示のためにペインを動かさない。
/// レイアウトモード（タイル表示／PaneHost）とは表示の差し替えだけで切替わり、
/// レイアウトツリー（_root）には一切触れない — レイアウトへ戻せば元のタイル配置・比率がそのまま戻る。
/// 「俯瞰」は全セッションをカードで一望する Exposé 風レイヤ（クリックで舞台へダイブ）。
/// ソロ中に <c>FocusPane</c> が呼ばれると対象が自動で舞台に立つので、AI がファイルを
/// 開いた・差分を出した等の既存フローがそのまま「舞台の自動転換」になる。
/// </summary>
public partial class ShellWindow {

    private readonly StageModeCoordinator _stageMode = new();
    private bool _stageActive { get => _stageMode.Active; set => _stageMode.Active = value; }
    private PaneKind _stagePane { get => _stageMode.Pane; set => _stageMode.Pane = value; }

    private bool OnStage(PaneKind kind) => _stageMode.IsOnStage(kind);

    private bool _overviewActive { get => _stageMode.Overview; set => _stageMode.Overview = value; }
    private DispatcherTimer? _stageResizeTimer;
    private Size _stageBuiltSize;
    private readonly Dictionary<PaneKind, Grid> _stageThumbnailHosts = new();

    private HashSet<PaneKind> _enabledSessions => _stageMode.EnabledSessions;

    private bool _idePaneApplicable { get => _stageMode.IdePaneApplicable; set => _stageMode.IdePaneApplicable = value; }

    private const double WingColumnReserve = 210;

    private Point _wingDragStart;
    private bool _wingDragArmed;

    private static readonly PaneKind[] StageOrder =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport,
        PaneKind.Git, PaneKind.Diff, PaneKind.Ai, PaneKind.Debug,
    ];

    private void OnToggleStageMode(object sender, RoutedEventArgs e) => ToggleDisplayMode();

    private void ToggleDisplayMode() {
        BeginTrailLayoutChange();
        if (_stageActive)
            ExitStageMode();   // → レイアウトモード
        else
            EnterStageMode();  // → ソロモード
    }

    private void EnterStageMode()
        => EnterStageMode(null);

    private void EnterStageMode(PaneKind? pane) {
        var selectedPane = pane is { } requested && _paneElements.ContainsKey(requested)
            ? requested
            : _focusedRegion?.Pane
            ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind
            ?? PaneKind.Editor;
        if (!_stageMode.Enter(selectedPane))
            return;
        _zoomedPane = null;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        UpdateModeButtons();
        RebuildStage();
        FocusPane(_stagePane);
        SaveActiveWorkspaceSnapshot();
    }

    private void ClearStageModeForWorkspaceSwitch() {
        if (!_stageMode.Exit())
            return;
        StageHost.SizeChanged -= OnStageHostSizeChanged;
        _stageResizeTimer?.Stop();
        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        WingStrip.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        OverviewLayer.Visibility = Visibility.Collapsed;
        StageHost.Visibility = Visibility.Collapsed;
        PaneHost.Opacity = 1;
        PaneHost.IsHitTestVisible = true;
        PaneToggleBar.Visibility = Visibility.Visible;
        UpdateModeButtons();
    }

    private void PrepareStageSnapshot(bool solo, StageSnapshot? snapshot) {
        ClearStageModeForWorkspaceSwitch();
        if (!solo)
            return;

        snapshot ??= StageSnapshot.Default();
        var restoredPane = snapshot.Pane is { } requested && _paneElements.ContainsKey(requested)
            && (requested != PaneKind.Debug || _idePaneApplicable)
            ? requested
            : PaneKind.Editor;
        _stageMode.Restore(active: true, overview: snapshot.Overview, pane: restoredPane);
        _zoomedPane = null;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        UpdateModeButtons();
    }

    private void CompleteStageSnapshotRestore() {
        if (!_stageActive)
            return;

        RebuildStage();
        if (_stagePane == PaneKind.EditorSupport)
            _ = UpdateEditorSupportAsync();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            if (_stageActive)
                FocusPane(_stagePane);
        }));
    }

    private void ExitStageMode() {
        if (!_stageMode.Exit())
            return;
        StageHost.SizeChanged -= OnStageHostSizeChanged;
        _stageResizeTimer?.Stop();
        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        WingStrip.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        OverviewLayer.Visibility = Visibility.Collapsed;
        StageHost.Visibility = Visibility.Collapsed;
        PaneHost.Opacity = 1;
        PaneHost.IsHitTestVisible = true;
        PaneToggleBar.Visibility = Visibility.Visible;
        UpdateModeButtons();
        RebuildPaneLayout();
        FocusPane(_stagePane);
        if (IsPaneVisible(PaneKind.Terminal))
            MarkPaneActivitySeen(PaneKind.Terminal);
        SaveActiveWorkspaceSnapshot();
    }

    private void SetStagePane(PaneKind kind) {
        if (!_stageMode.Select(kind))
            return;
        BeginTrailLayoutChange();
        RebuildStage();
        if (kind == PaneKind.EditorSupport)
            _ = UpdateEditorSupportAsync();
        MarkPaneActivitySeen(kind);   // 舞台に立った＝目に入ったので未確認バッジを流す
        SaveActiveWorkspaceSnapshot();
    }

    private void CycleInActiveMode(int direction) {
        if (_stageActive)
            CycleStage(direction);
        else
            CycleLayout(direction);
    }

    private void CycleStage(int direction) {
        var index = Array.IndexOf(StageOrder, _stagePane);
        var next = StageOrder[((index < 0 ? 0 : index) + direction + StageOrder.Length) % StageOrder.Length];
        SetStagePane(next);
        FocusPane(next);
    }

    private void DetachPaneElements() {
        foreach (var element in _paneElements.Values)
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);
    }

    private Size StageVirtualSize() {
        if (StageArea.ActualWidth > 0 && StageArea.ActualHeight > 0)
            return new Size(StageArea.ActualWidth, StageArea.ActualHeight);
        var hostW = StageHost.ActualWidth > 0 ? StageHost.ActualWidth : PaneHost.ActualWidth;
        var hostH = StageHost.ActualHeight > 0 ? StageHost.ActualHeight : PaneHost.ActualHeight;
        return new Size(
            Math.Max(hostW - WingColumnReserve - 16, 480),   // 16 ≒ StageArea の左右マージン
            Math.Max(hostH - 18, 320));                      // 18 ≒ 上下マージン
    }

    private void OnStageHostSizeChanged(object sender, SizeChangedEventArgs e) {
        if (!_stageActive)
            return;
        if (_stageResizeTimer is null) {
            _stageResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _stageResizeTimer.Tick += (_, _) => {
                _stageResizeTimer!.Stop();
                if (!_stageActive)
                    return;
                var size = StageVirtualSize();
                if (Math.Abs(size.Width - _stageBuiltSize.Width) > 1
                    || Math.Abs(size.Height - _stageBuiltSize.Height) > 1)
                    RebuildStage();
            };
        }
        _stageResizeTimer.Stop();
        _stageResizeTimer.Start();
    }

    private bool IsSessionEnabled(PaneKind kind) => _enabledSessions.Contains(kind);

    private bool IsShownInMain(PaneKind kind) {
        if (_stageActive)
            return !_overviewActive && OnStage(kind);
        if (_zoomedPane is { } zoom)
            return zoom == kind && IsPaneVisible(kind);
        return IsPaneVisible(kind);
    }

    private void LoadEnabledSessions(IEnumerable<PaneKind>? enabled) {
        _enabledSessions.Clear();
        if (enabled is not null)
            foreach (var kind in enabled)
                if (_paneElements.ContainsKey(kind))
                    _enabledSessions.Add(kind);
        if (_enabledSessions.Count == 0)
            foreach (var kind in StageOrder)
                _enabledSessions.Add(kind);
        if (!_idePaneApplicable)
            _enabledSessions.Remove(PaneKind.Debug);
    }

    private void ApplyIdePaneApplicability(string? root) {
        _idePaneApplicable = ViewModels.DebugTargetResolver.HasCSharpProject(root);
        DebugPaneToggle.Visibility = _idePaneApplicable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleSessionEnabled(PaneKind kind) {
        if (_enabledSessions.Contains(kind)) {
            if (IsPaneVisible(kind)) {
                SetPaneVisible(kind, false);
                if (IsPaneVisible(kind))
                    return;
            }
            _enabledSessions.Remove(kind);
            RebuildSessionsView();
        } else {
            _enabledSessions.Add(kind);
            if (FindLeaf(kind) is { Hidden: true })
                SetPaneVisible(kind, true);
            else
                RebuildSessionsView();
        }
    }

    private void RebuildSessionsView() {
        UpdatePaneToggleStates();
        if (_stageActive)
            RebuildStage();
        else {
            ScheduleLayoutWings();
        }
        SaveActiveWorkspaceSnapshot();
    }

    private IEnumerable<PaneKind> OverviewKinds() => StageOrder.Where(k => IsSessionEnabled(k) || OnStage(k));

    private void RebuildStage() {
        if (!_stageActive)
            return;

        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        _stageActivityBadges.Clear();

        var virtualSize = StageVirtualSize();
        _stageBuiltSize = virtualSize;
        BuildStageThumbnailSources(virtualSize);

        if (_overviewActive) {
            OverviewLayer.Visibility = Visibility.Visible;
            foreach (var kind in OverviewKinds())
                OverviewPanel.Children.Add(BuildSessionCard(kind, OverviewCardWidth, isOverview: true));
        } else {
            OverviewLayer.Visibility = Visibility.Collapsed;
            StageArea.Children.Add(BuildLiveSlot(_stagePane));
        }

        RebuildWings();
        UpdatePaneToggleStates();
        UpdateWingHostVisibility();
        ScheduleBrowserRealize(_activeBrowserTab);
    }

    private void OnToggleOverview(object sender, RoutedEventArgs e) => ToggleOverview();

    private void ToggleOverview() {
        if (!_stageActive)
            return;
        _overviewActive = !_overviewActive;
        RebuildStage();
    }

    private void OnOverviewBackgroundClick(object sender, MouseButtonEventArgs e) {
        if (_overviewActive) {
            _overviewActive = false;
            RebuildStage();
        }
    }
}
