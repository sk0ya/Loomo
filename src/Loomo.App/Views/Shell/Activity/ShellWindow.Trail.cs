namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: 軌跡（操作ログ）バーの配線。エディタのファイル活性化・ブラウザ遷移・ ペイン／パネル切替を <see cref="TrailViewModel"/> へ記録し、ドットのクリックや バー上の Shift+ホイール（現在地の前後移動）でその地点へ戻る（素のホイールはバーの水平スクロール）。 バー左端の日付クリック→カレンダーで 過去の日の軌跡も表示できる。アイデア.md「Semantic Depth」構想の Thread Rail の種。 <para><b>新しい軌跡ソースの足し方（登録側はこれだけ）</b>： ①<see cref="TrailEntryKind"/> に enum 値を1つ追加し <c>Glyph</c>（ツールチップ・一意性テスト用）と <c>IconGeometry</c>（バーに描く絵姿）を対で1本ずつ足す。 ②<see cref="RegisterTrailJumps"/> にその種別の「戻る」処理を1行登録する。 ③記録したい場所（イベントハンドラ等）で <see cref="RecordTrail"/> を呼ぶ。 記録の抑制（復元・ジャンプ中）と離脱位置の上書きは <see cref="RecordTrail"/> が共通で面倒を見るので、 各ソースはこの3点以外を書かなくてよい。</para></summary>
public partial class ShellWindow {
    private TrailJumpCoordinator _trailJumpCoordinator = null!;
    private readonly sk0ya.Loomo.Services.GitService _git;
    private bool _trailSuppressed;
    private TrailPaneCommitController _trailPaneCommit = null!;
    private TrailEditCommitController _trailEditCommit = null!;
    private TrailBarController _trailBar = null!;
    private bool _trailBrowsingPast {
        get => _trailBar.BrowsingPast;
        set => _trailBar.BrowsingPast = value;
    }
    private string? _trailLastLayoutKey;
    private void InitializeTrail() {
        RegisterTrailJumps();
        _trailEditCommit = new TrailEditCommitController(_vm.Trail, () => _trailSuppressed, RecordTrail);
        _trailPaneCommit = new TrailPaneCommitController(
            () => _trailSuppressed,
            () => _stageActive,
            () => CurrentDisplayMode,
            () => _focusedRegion?.Pane,
            CommitTrailPane);
        _vm.Trail.JumpRequested += (_, entry) => JumpToTrailEntry(entry);
        _vm.AiBar.SessionActivated += (_, e) => RecordTrailSession(e.Id, e.Title);
        _git.OperationExecuted += (_, e) =>
            Dispatcher.BeginInvoke(new Action(() => RecordTrailGit(e.Command, e.Success)));
        _vm.Trail.Entries.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => {
                if (!_trailBrowsingPast)
                    ScrollTrailToCurrent();
            }), DispatcherPriority.Loaded);
        TrailScroll.SizeChanged += (_, _) => _trailBar.UpdateTrailingMargin();
        TrailDateTimePopup.Closed += (_, _) => _trailBar.PopupClosed();
        Deactivated += (_, _) => { if (TrailDateTimePopup.IsOpen) TrailDateTimePopup.IsOpen = false; };
        _trailHourTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _trailHourTicker.Tick += (_, _) => _vm.Trail.RefreshHourLabel();
        _trailHourTicker.Start();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _vm.Trail.EnsureLoaded()));
    }
    private DispatcherTimer? _trailHourTicker;
    private void RecordTrail(Action<DisplayMode, PaneKind?, string?> record) {
        if (_trailSuppressed)
            return;
        RefreshLatestTrailFilePosition();
        var mode = CurrentDisplayMode;
        var paneLayout = TrailLogic.SerializeLayout(_root is null ? null : ToSnapshot(_root));
        _vm.Trail.UpdateLatestPaneLayout(paneLayout);
        record(mode, _stageActive ? _stagePane : null, paneLayout);
    }
    private void RefreshLatestTrailPaneLayout() {
        if (_trailSuppressed)
            return;
        var paneLayout = TrailLogic.SerializeLayout(_root is null ? null : ToSnapshot(_root));
        _vm.Trail.UpdateLatestPaneLayout(paneLayout);
    }
    private void RecordTrailLayoutIfChanged() {
        var state = CurrentTrailLayoutState();
        var transition = TrailLogic.AdvanceLayoutKey(_trailLastLayoutKey, state.Key, _trailSuppressed);
        _trailLastLayoutKey = transition.Key;
        if (!transition.ShouldRecord)
            return;
        var label = TrailLogic.LayoutChangeLabel(state.Mode, state.StagePane);
        RecordTrail((recordMode, recordStagePane, layout) =>
            _vm.Trail.RecordLayout(state.Key, label, recordMode, recordStagePane, layout));
    }
    private void BeginTrailLayoutChange() {
        _trailLastLayoutKey = CurrentTrailLayoutState().Key;
    }
    private TrailLayoutState CurrentTrailLayoutState() {
        var mode = CurrentDisplayMode;
        var stagePane = _stageActive ? _stagePane : (PaneKind?)null;
        var snapshot = _root is null ? null : ToSnapshot(_root);
        return TrailLogic.CreateLayoutState(mode, stagePane, snapshot, CurrentDockKey());
    }
    private void RecordTrailEditorTab(EditorTab tab) {
        var path = tab.PeekFilePath;
        if (!TrailLogic.IsRecordableFile(path, tab.PeekIsVirtual))
            return;
        var line = -1;
        var column = -1;
        if (tab.IsRealized) {
            line = tab.Control.Caret.Line;
            column = tab.Control.Caret.Column;
        }
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordFile(path, line, column, mode, stagePane, layout));
    }
    private void RefreshLatestTrailFilePosition() {
        if (_vm.Trail.LatestFileTarget is not { } target)
            return;
        var tab = _editorTabs.FirstOrDefault(t => t.IsRealized
            && string.Equals(t.PeekFilePath, target, StringComparison.OrdinalIgnoreCase));
        if (tab is not null)
            _vm.Trail.UpdateLatestFilePosition(target, tab.Control.Caret.Line, tab.Control.Caret.Column);
    }
    private void RecordTrailGit(string command, bool success) {
        if (!success)
            return;
        var (key, label) = TrailLogic.DescribeGitOperation(command);
        if (string.IsNullOrEmpty(key))
            return;
        RecordTrail((mode, stagePane, _) =>
            _vm.Trail.RecordGit(key, label, mode, stagePane));
    }
    private void RecordTrailPreview(EditorTab? sourceTab) {
        var path = sourceTab?.PeekFilePath;
        if (!TrailLogic.IsRecordableFile(path, sourceTab?.PeekIsVirtual ?? true))
            return;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordPreview(path, mode, stagePane, layout));
    }
    private void RecordTrailBrowser(string? url, string? title) {
        if (!TrailLogic.IsRecordableBrowserUrl(url, DefaultBrowserUrl))
            return;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordBrowser(url!, title, mode, stagePane, layout));
    }
    private void RecordTrailTerminalTab(TerminalTab tab) {
        var label = TrailLogic.TerminalLabel(
            _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title,
            tab.View.HeaderTitle);
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordTerminal(tab.Id, label, mode, stagePane, layout));
    }
    private void RecordTrailPane(PaneKind kind) => _trailPaneCommit.Request(kind);

    private void CommitTrailPane(PaneKind kind) {
        var editor = _activeEditorTab;
        var terminal = _activeTerminalTab;
        var preview = _editorSupport.Source;
        var line = editor is { IsRealized: true } ? editor.Control.Caret.Line : -1;
        var column = editor is { IsRealized: true } ? editor.Control.Caret.Column : -1;
        var terminalLabel = terminal is null ? null : TrailLogic.TerminalLabel(
            _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == terminal.Id)?.Title,
            terminal.View.HeaderTitle);
        var target = TrailLogic.CreatePaneRecordTarget(
            kind, editor?.PeekFilePath, editor?.PeekIsVirtual ?? true, line, column,
            terminal?.Id, terminalLabel, preview?.PeekFilePath, preview?.PeekIsVirtual ?? true,
            BrowserUrlOf(_activeBrowserTab), _activeBrowserTab?.View.TryCore()?.DocumentTitle,
            DefaultBrowserUrl);
        RecordTrail((recordMode, recordStagePane, recordLayout) =>
            _vm.Trail.Record(target.Kind, target.Target, target.Label, target.Line, target.Column,
                recordMode, recordStagePane, recordLayout));
    }
    private void RecordTrailSession(string id, string title) {
        if (string.IsNullOrWhiteSpace(id))
            return;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordSession(id, title, mode, stagePane, layout));
    }
    private void RecordTrailPanel(SidebarPanel panel)
        => RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordPanel(panel.ToString(), TrailLogic.PanelDisplayName(panel), mode, stagePane, layout));
    private void RegisterTrailJumps() {
        _trailJumpCoordinator = new TrailJumpCoordinator(
            _vm.Trail,
            CanJumpToTrailEntry,
            RestoreTrailDisplayContext,
            () => _trailSuppressed,
            value => _trailSuppressed = value,
            value => _trailBrowsingPast = value);
        _trailJumpCoordinator.Register(TrailEntryKind.File, JumpToFileAsync);
        _trailJumpCoordinator.Register(TrailEntryKind.Browser, entry => { JumpToBrowser(entry); return Task.CompletedTask; });
        _trailJumpCoordinator.Register(TrailEntryKind.Pane, entry => { JumpToPane(entry); return Task.CompletedTask; });
        _trailJumpCoordinator.Register(TrailEntryKind.Panel, entry => { JumpToPanel(entry); return Task.CompletedTask; });
        _trailJumpCoordinator.Register(TrailEntryKind.Terminal, entry => { JumpToTerminal(entry); return Task.CompletedTask; });
        _trailJumpCoordinator.Register(TrailEntryKind.Preview, JumpToPreviewAsync);
        _trailJumpCoordinator.Register(TrailEntryKind.Session, entry => { JumpToSession(entry); return Task.CompletedTask; });
        _trailJumpCoordinator.Register(TrailEntryKind.Layout, _ => Task.CompletedTask);
        _trailJumpCoordinator.Register(TrailEntryKind.Edit, JumpToFileAsync);
        _trailJumpCoordinator.Register(TrailEntryKind.Git, _ => Task.CompletedTask);
    }
    private void JumpToTrailEntry(TrailEntryViewModel entry) {
        _trailJumpCoordinator.Request(entry);
    }
    private bool CanJumpToTrailEntry(TrailEntryViewModel entry) => TrailLogic.CanJumpToEntry(
        entry.Kind,
        entry.Target,
        entry.PaneLayout,
        pane => _paneElements.ContainsKey(pane),
        id => _terminalTabs.Any(t => t.Id == id),
        id => _vm.AiBar.SessionExists(id));
    private void RestoreTrailDisplayContext(TrailEntryViewModel entry) {
        if (_stageActive)
            ExitStageMode();
        if (!string.IsNullOrWhiteSpace(entry.PaneLayout)) {
            // 壊れた1件だけ配置復元を省略し、対象へのジャンプは続ける。
            if (TrailLogic.TryDeserializeLayout(entry.PaneLayout, out var snapshot) && snapshot is not null)
                ApplyPaneLayout(snapshot);
        }
        if (entry.Mode == DisplayMode.Solo) {
            EnterStageMode(entry.StagePane);
        }
    }
    private async Task JumpToFileAsync(TrailEntryViewModel entry) {
        if (!File.Exists(entry.Target))
            return;   // 消えたファイルはそっと何もしない（ブランチ切替等で戻ることもある）
        await OpenFileInNewEditorTabAsync(entry.Target);
        FocusPane(PaneKind.Editor);
        if (entry.Line >= 0)
            _activeEditorTab?.Control.NavigateTo(entry.Line, Math.Max(0, entry.Column));
    }
    private async Task JumpToPreviewAsync(TrailEntryViewModel entry) {
        if (!File.Exists(entry.Target))
            return;   // 消えたファイルはそっと何もしない（ブランチ切替等で戻ることもある）
        await OpenFileInNewEditorTabAsync(entry.Target);
        if (_activeEditorTab is { } tab)
            OpenEditorSupport(tab);
    }
    private void JumpToBrowser(TrailEntryViewModel entry) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        FocusPane(PaneKind.Browser);
        NavigateBrowser(entry.Target);
    }
    private void JumpToPane(TrailEntryViewModel entry) {
        if (!Enum.TryParse<PaneKind>(entry.Target, out var pane))
            return;
        EnsurePaneVisibleOrSwapTopLeft(pane);
        FocusPane(pane);
        _trailPaneCommit.Remember(pane, entry.Mode);
    }
    private void JumpToPanel(TrailEntryViewModel entry) {
        if (!Enum.TryParse<SidebarPanel>(entry.Target, out var panel))
            return;
        _vm.RestorePanel(panel);   // 出せなくなったパネル（C# の消えた部屋）はそっと何もしない
    }
    private void JumpToSession(TrailEntryViewModel entry) {
        if (!_vm.AiBar.RestoreSessionById(entry.Target))
            return;   // 削除済みセッションは復元不能なので何もしない
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
        FocusPane(PaneKind.Ai);
    }
    private void JumpToTerminal(TrailEntryViewModel entry) {
        if (!Guid.TryParse(entry.Target, out var id) || _terminalTabs.All(t => t.Id != id))
            return;   // 閉じられたタブは復元不能なので何もしない
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
        ActivateTerminalTab(id);
        FocusPane(PaneKind.Terminal);
    }
    private void OnTrailWheel(object sender, MouseWheelEventArgs e) => _trailBar.OnWheel(e);
    private void ScrollTrailToCurrent() => _trailBar.ScrollToCurrent();
    private void UpdateTrailTrailingMargin() => _trailBar.UpdateTrailingMargin();
    private void OnTrailBackToLatest(object sender, RoutedEventArgs e) => _trailBar.BackToLatest();
    private void OnTrailBackToLatestFromPopup(object sender, RoutedEventArgs e) => _trailBar.BackToLatestFromPopup();
    private void OnTrailDateTimeClick(object sender, RoutedEventArgs e) => _trailBar.ToggleDateTimePopup();
    private void OnTrailCalendarSelected(object? sender, SelectionChangedEventArgs e) => _trailBar.SelectCalendarDate();
    private void OnTrailHourSelected(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { DataContext: TrailHourViewModel hour })
            _trailBar.SelectHour(hour);
    }
    private void OnTrailDateTimeLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _trailBar.ClosePopupIfFocusLeaves(e);
}
