namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: 表示モード（分割／ドック）の切り替えと、レイアウトモードの右端の「袖」（ペインの
/// VisualBrush による縮小表示）の状態。袖カードは実コントロールを子に持たず元の表示を描くだけなので、
/// 袖表示のためにペインを動かさない。かつてあったソロ（舞台＋袖＋俯瞰）は 2026-11-14 に外した——保存済みの
/// ソロは分割として開く（<see cref="WorkspaceSessionCoordinator.ResolveDisplayMode"/>）。</summary>
public partial class ShellWindow {
    private readonly StageModeCoordinator _stageMode = new();
    private HashSet<PaneKind> _enabledSessions => _stageMode.EnabledSessions;
    /// <summary>袖で選択中のタブ（メイン／サブ／すべて）。所属は固定
    /// （<see cref="StageModeCoordinator.MainGroup"/>）で、切り替えるのは「どれを見るか」だけ。</summary>
    private WingTab _activeWingTab {
        get => _stageMode.ActiveWingTab;
        set => _stageMode.ActiveWingTab = value;
    }
    private bool InActiveWingTab(PaneKind kind) => _stageMode.IsInActiveWingTab(kind);
    private bool _idePaneApplicable { get => _stageMode.IdePaneApplicable; set => _stageMode.IdePaneApplicable = value; }
    private bool _tsIdePaneApplicable { get => _stageMode.TsIdePaneApplicable; set => _stageMode.TsIdePaneApplicable = value; }
    private const double DefaultWingWidth = 250;
    // アイコンボタン（40px）＋左右の最小余白だけを確保する幅。
    private const double CollapsedWingWidth = 48;
    private const double MinWingWidth = 180;
    private const double MaxWingWidth = 800;
    private double _wingWidth = DefaultWingWidth;
    private bool _isWingCollapsed;
    private double EffectiveWingWidth => _isWingCollapsed ? CollapsedWingWidth : _wingWidth;
    /// <summary>いまの表示モード（分割／ドック）。<c>_dockActive</c> から判断する場所をここ以外に作らない。</summary>
    private DisplayMode CurrentDisplayMode => _dockActive ? DisplayMode.Dock : DisplayMode.Layout;
    // モード切替は「まだ選んでいる途中」の操作（切り替えてからメイン画面を選び直すことが多い）なので
    // ポップアップは閉じず、中身だけ作り直す。閉じるのは行き先を決める操作（メイン画面・配置）だけ。
    private void OnChooseSplitMode(object sender, RoutedEventArgs e) {
        if (_dockActive) {
            BeginTrailLayoutChange();
            ExitDockMode();
        }
        RefreshOpenPaneMenu();
    }
    /// <summary>ショートカット（mode.toggle）は2モードの往復：分割 ⇔ ドック。</summary>
    private void ToggleDisplayMode() {
        BeginTrailLayoutChange();
        if (_dockActive)
            ExitDockMode();
        else
            EnterDockMode();
    }
    /// <summary>袖の幅と折りたたみをワークスペースの保存値へ戻す（ソロ時代の <see cref="StageSnapshot"/> に同居している）。</summary>
    private void RestoreWingSnapshot(StageSnapshot? snapshot) {
        _wingWidth = Math.Clamp(snapshot?.WingWidth ?? DefaultWingWidth, MinWingWidth, MaxWingWidth);
        _isWingCollapsed = snapshot?.WingCollapsed ?? false;
    }
    private void CycleInActiveMode(int direction) {
        if (_dockActive)
            CycleDockCenter(direction);   // ドックは中央に立てる面を順に送る
        else
            CycleLayout(direction);
    }
    /// <summary>ペインを現在の親から外す。<paramref name="keep"/>（＝袖ミニチュアに据え置くもの）は触らない。</summary>
    private void DetachPaneElementsExcept(IReadOnlyCollection<PaneKind> keep) {
        foreach (var (kind, element) in _paneElements)
            if (!keep.Contains(kind) && element.Parent is Panel parent)
                parent.Children.Remove(element);
    }
    private void SetWingWidth(double width) {
        _wingWidth = Math.Clamp(width, MinWingWidth, MaxWingWidth);
        if (WingHost.Visibility == Visibility.Visible)
            WingColumn.Width = new GridLength(EffectiveWingWidth);
        RebuildWings();
        SaveActiveWorkspaceSnapshot();
    }
    private bool IsSessionEnabled(PaneKind kind) => _enabledSessions.Contains(kind);
    private bool IsShownInMain(PaneKind kind) {
        if (_dockActive)
            return IsDockPaneShown(kind);
        if (_zoomedPane is { } zoom)
            return zoom == kind && IsPaneVisible(kind);
        return IsPaneVisible(kind);
    }
    private void LoadEnabledSessions(IEnumerable<PaneKind>? enabled) {
        _stageMode.LoadEnabledSessions(enabled, _paneElements.Keys, StageModeCoordinator.DefaultEnabledSessions);
    }
    private void OnWingTabClick(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<WingTab>(tag, out var tab))
            SelectWingTab(tab);
    }
    private void SelectWingTab(WingTab tab) {
        if (!_stageMode.SelectWingTab(tab)) {
            UpdateWingTabs();   // 選択中のタブを押しても選択は外れない（ToggleButton の解除を戻す）
            return;
        }
        UpdateWingTabs();       // レイアウトモードの袖組み直しは ContextIdle 送りなので、印だけ先に合わせる
        RebuildSessionsView();
    }
    private void ApplyIdePaneApplicability(IReadOnlyList<string> folders) {
        _idePaneApplicable = ViewModels.DebugTargetResolver.HasCSharpProject(folders);
        _tsIdePaneApplicable = ViewModels.TsDebugTargetResolver.HasTypeScriptProject(folders);
        UpdateDebugButtonVisibility();   // タイトルバーのデバッグメニューも同じ判定で出し入れする
        RefreshOpenPaneMenu();   // 行の集合が変わるので、開いていれば作り直す
    }
    private void ToggleSessionEnabled(PaneKind kind) {
        if (_stageMode.ToggleEnabledSession(
                kind,
                () => IsPaneVisible(kind),
                () => SetPaneVisible(kind, false),
                () => FindLeaf(kind) is { Hidden: true },
                () => SetPaneVisible(kind, true)))
            RebuildSessionsView();
    }
    private void RebuildSessionsView() {
        UpdatePaneToggleStates();
        if (_dockActive)
            RebuildDock();      // 袖なしなので組み直すのは帯とドックの領域
        else
            ScheduleLayoutWings();
        SaveActiveWorkspaceSnapshot();
    }
}
