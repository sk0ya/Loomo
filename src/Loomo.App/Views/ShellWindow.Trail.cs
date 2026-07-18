namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: 軌跡（操作ログ）バーの配線。エディタのファイル活性化・ブラウザ遷移・ ペイン／パネル切替を <see cref="TrailViewModel"/> へ記録し、ドットのクリックや バー上の Shift+ホイール（現在地の前後移動）でその地点へ戻る（素のホイールはバーの水平スクロール）。 バー左端の日付クリック→カレンダーで 過去の日の軌跡も表示できる。アイデア.md「Semantic Depth」構想の Thread Rail の種。 <para><b>新しい軌跡ソースの足し方（登録側はこれだけ）</b>： ①<see cref="TrailEntryKind"/> に enum 値を1つ追加し <c>Glyph</c>（ツールチップ・一意性テスト用）と <c>IconGeometry</c>（バーに描く絵姿）を対で1本ずつ足す。 ②<see cref="RegisterTrailJumps"/> にその種別の「戻る」処理を1行登録する。 ③記録したい場所（イベントハンドラ等）で <see cref="RecordTrail"/> を呼ぶ。 記録の抑制（復元・ジャンプ中）と離脱位置の上書きは <see cref="RecordTrail"/> が共通で面倒を見るので、 各ソースはこの3点以外を書かなくてよい。</para></summary>
public partial class ShellWindow {
    private readonly Dictionary<TrailEntryKind, Func<TrailEntryViewModel, Task>> _trailJumps = new();
    private readonly sk0ya.Loomo.Services.GitService _git;
    private bool _trailSuppressed;
    private PaneKind? _trailLastPane;
    private DisplayMode? _trailLastPaneMode;
    private DispatcherTimer? _trailPaneCommitTimer;
    private PaneKind? _trailPendingPane;
    private DispatcherTimer? _trailEditCommitTimer;
    private EditorTab? _trailPendingEditTab;
    private TrailBarController _trailBar = null!;
    private bool _trailBrowsingPast {
        get => _trailBar.BrowsingPast;
        set => _trailBar.BrowsingPast = value;
    }
    private TrailEntryViewModel? _trailPendingJumpEntry;
    private bool _trailJumpRunning;
    private DispatcherTimer? _trailJumpSettleTimer;
    private bool _trailJumpBaseSuppressed;
    private string? _trailLastLayoutKey;
    private void InitializeTrail() {
        RegisterTrailJumps();
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
        var mode = _stageActive ? DisplayMode.Solo : DisplayMode.Layout;
        var paneLayout = _root is null ? null : JsonSerializer.Serialize(ToSnapshot(_root), TrailLayoutJson);
        _vm.Trail.UpdateLatestPaneLayout(paneLayout);
        record(mode, _stageActive ? _stagePane : null, paneLayout);
    }
    private void RefreshLatestTrailPaneLayout() {
        if (_trailSuppressed)
            return;
        var paneLayout = _root is null ? null : JsonSerializer.Serialize(ToSnapshot(_root), TrailLayoutJson);
        _vm.Trail.UpdateLatestPaneLayout(paneLayout);
    }
    private void RecordTrailLayoutIfChanged() {
        var (layoutKey, mode, stagePane, paneLayout) = CurrentTrailLayoutState();
        if (_trailLastLayoutKey is null) {
            _trailLastLayoutKey = layoutKey;
            return;
        }
        if (string.Equals(_trailLastLayoutKey, layoutKey, StringComparison.Ordinal))
            return;
        _trailLastLayoutKey = layoutKey;
        if (_trailSuppressed)
            return;
        var label = mode == DisplayMode.Solo
            ? $"ソロ · {TrailLogic.PaneDisplayName(stagePane ?? PaneKind.Editor)}"
            : "レイアウト変更";
        RecordTrail((recordMode, recordStagePane, layout) =>
            _vm.Trail.RecordLayout(layoutKey, label, recordMode, recordStagePane, layout));
    }
    private void BeginTrailLayoutChange() {
        _trailLastLayoutKey = CurrentTrailLayoutState().Key;
    }
    private (string Key, DisplayMode Mode, PaneKind? StagePane, string? PaneLayout) CurrentTrailLayoutState() {
        var mode = _stageActive ? DisplayMode.Solo : DisplayMode.Layout;
        var stagePane = _stageActive ? _stagePane : (PaneKind?)null;
        var snapshot = _root is null ? null : ToSnapshot(_root);
        var paneLayout = snapshot is null ? null : JsonSerializer.Serialize(snapshot, TrailLayoutJson);
        var key = TrailLogic.LayoutKey(mode, stagePane, snapshot);
        return (key, mode, stagePane, paneLayout);
    }
    private void RecordTrailEditorTab(EditorTab tab) {
        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
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
    private void RecordTrailEdit(EditorTab tab) {
        if (_trailSuppressed || !tab.IsRealized || !tab.Control.IsModified)
            return;
        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;
        if (_trailPendingEditTab is { } pending && !ReferenceEquals(pending, tab))
            CommitTrailEdit();
        _trailPendingEditTab = tab;
        _trailEditCommitTimer ??= CreateTrailEditCommitTimer();
        _trailEditCommitTimer.Stop();
        _trailEditCommitTimer.Start();
    }
    private DispatcherTimer CreateTrailEditCommitTimer() {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) => {
            timer.Stop();
            CommitTrailEdit();
        };
        return timer;
    }
    private void CommitTrailEdit() {
        _trailEditCommitTimer?.Stop();
        if (_trailPendingEditTab is not { } tab)
            return;
        _trailPendingEditTab = null;
        if (_trailSuppressed || !tab.IsRealized || !tab.Control.IsModified)
            return;
        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;
        var line = tab.Control.Caret.Line;
        var column = tab.Control.Caret.Column;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordEdit(path, line, column, mode, stagePane, layout));
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
        if (string.IsNullOrWhiteSpace(path) || sourceTab!.PeekIsVirtual)
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
    private string? CurrentBrowserTrailUrl() {
        var url = _activeBrowserTab?.View.Source?.ToString() ?? _activeBrowserTab?.PendingUrl;
        if (!TrailLogic.IsRecordableBrowserUrl(url, DefaultBrowserUrl))
            return null;
        return url;
    }
    private void RecordTrailTerminalTab(TerminalTab tab) {
        var label = _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title;
        if (string.IsNullOrWhiteSpace(label))
            label = string.IsNullOrWhiteSpace(tab.View.HeaderTitle) ? "ターミナル" : tab.View.HeaderTitle;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordTerminal(tab.Id, label, mode, stagePane, layout));
    }
    private void RecordTrailPane(PaneKind kind) {
        if (_trailSuppressed || _stageActive)
            return;
        var mode = DisplayMode.Layout;
        if (_trailLastPane == kind && _trailLastPaneMode == mode) {
            _trailPendingPane = null;
            _trailPaneCommitTimer?.Stop();
            return;
        }
        _trailPendingPane = kind;
        _trailPaneCommitTimer ??= CreateTrailPaneCommitTimer();
        _trailPaneCommitTimer.Stop();
        _trailPaneCommitTimer.Start();
    }
    private DispatcherTimer CreateTrailPaneCommitTimer() {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) => {
            timer.Stop();
            if (_trailPendingPane is not { } kind)
                return;
            _trailPendingPane = null;
            var mode = DisplayMode.Layout;
            if (_trailSuppressed
                || _stageActive
                || (_trailLastPane == kind && _trailLastPaneMode == mode)
                || _focusedRegion?.Pane != kind)
                return;
            _trailLastPane = kind;
            _trailLastPaneMode = mode;
            if (kind == PaneKind.Editor && _activeEditorTab is { } et
                && !string.IsNullOrWhiteSpace(et.PeekFilePath) && !et.PeekIsVirtual) {
                RecordTrailEditorTab(et);
                return;
            }
            if (kind == PaneKind.Terminal && _activeTerminalTab is { } tt) {
                RecordTrailTerminalTab(tt);
                return;
            }
            if (kind == PaneKind.EditorSupport && _editorSupport.Source is { } est
                && !string.IsNullOrWhiteSpace(est.PeekFilePath) && !est.PeekIsVirtual) {
                RecordTrailPreview(est);
                return;
            }
            if (kind == PaneKind.Browser && CurrentBrowserTrailUrl() is { } browserUrl)
            {
                RecordTrailBrowser(browserUrl, _activeBrowserTab?.View.CoreWebView2?.DocumentTitle);
                return;
            }
            RecordTrail((mode, stagePane, layout) =>
                _vm.Trail.RecordPane(kind.ToString(), TrailLogic.PaneDisplayName(kind), mode, stagePane, layout));
        };
        return timer;
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
    private static readonly JsonSerializerOptions TrailLayoutJson = new();
    private void RegisterTrailJumps() {
        _trailJumps[TrailEntryKind.File] = JumpToFileAsync;
        _trailJumps[TrailEntryKind.Browser] = entry => { JumpToBrowser(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Pane] = entry => { JumpToPane(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Panel] = entry => { JumpToPanel(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Terminal] = entry => { JumpToTerminal(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Preview] = JumpToPreviewAsync;
        _trailJumps[TrailEntryKind.Session] = entry => { JumpToSession(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Layout] = _ => Task.CompletedTask;
        _trailJumps[TrailEntryKind.Edit] = JumpToFileAsync;
        _trailJumps[TrailEntryKind.Git] = _ => Task.CompletedTask;
    }
    private void JumpToTrailEntry(TrailEntryViewModel entry) {
        _trailPendingJumpEntry = entry; // 実行中なら中間要求を捨て、最後の要求だけ残す
        if (!_trailJumpRunning)
            ProcessTrailJumpsAsync();
    }
    private async void ProcessTrailJumpsAsync() {
        if (_trailJumpRunning)
            return;
        _trailJumpRunning = true;
        if (_trailJumpSettleTimer is not { IsEnabled: true })
            _trailJumpBaseSuppressed = _trailSuppressed;
        _trailJumpSettleTimer?.Stop();
        _trailSuppressed = true;
        try {
            while (_trailPendingJumpEntry is { } entry) {
                _trailPendingJumpEntry = null;
                if (!_trailJumps.TryGetValue(entry.Kind, out var jump) || !CanJumpToTrailEntry(entry))
                    continue;
                RestoreTrailDisplayContext(entry);
                await jump(entry);
            }
        } finally {
            _trailJumpRunning = false;
        }
        _trailBrowsingPast = _vm.Trail.CurrentIndex < _vm.Trail.Entries.Count - 1;
        _trailJumpSettleTimer ??= CreateTrailJumpSettleTimer();
        _trailJumpSettleTimer.Stop();
        _trailJumpSettleTimer.Start();
    }
    private DispatcherTimer CreateTrailJumpSettleTimer() {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => {
            timer.Stop();
            if (!_trailJumpRunning)   // 次のジャンプが走り出していれば、そちらの settle に任せる
                _trailSuppressed = _trailJumpBaseSuppressed;
        };
        return timer;
    }
    private bool CanJumpToTrailEntry(TrailEntryViewModel entry) {
        if (!string.IsNullOrWhiteSpace(entry.PaneLayout)) {
            try {
                if (JsonSerializer.Deserialize<PaneNodeSnapshot>(entry.PaneLayout, TrailLayoutJson) is null)
                    return false;
            } catch { return false; }
        }
        return entry.Kind switch {
            TrailEntryKind.File => File.Exists(entry.Target), TrailEntryKind.Browser => !string.IsNullOrWhiteSpace(entry.Target), TrailEntryKind.Pane => Enum.TryParse<PaneKind>(entry.Target, out var pane)
                                   && _paneElements.ContainsKey(pane), TrailEntryKind.Panel => Enum.TryParse<SidebarPanel>(entry.Target, out _), TrailEntryKind.Terminal => Guid.TryParse(entry.Target, out var id)
                                       && _terminalTabs.Any(t => t.Id == id), TrailEntryKind.Preview => File.Exists(entry.Target), TrailEntryKind.Session => _vm.AiBar.SessionExists(entry.Target), TrailEntryKind.Layout => !string.IsNullOrWhiteSpace(entry.PaneLayout), TrailEntryKind.Edit => File.Exists(entry.Target),
            TrailEntryKind.Git => false,   // ログ専用：クリックしても復元しない
            _ => false
        };
    }
    private void RestoreTrailDisplayContext(TrailEntryViewModel entry) {
        if (_stageActive)
            ExitStageMode();
        if (!string.IsNullOrWhiteSpace(entry.PaneLayout)) {
            try {
                var snapshot = JsonSerializer.Deserialize<PaneNodeSnapshot>(entry.PaneLayout, TrailLayoutJson);
                if (snapshot is not null)
                    ApplyPaneLayout(snapshot);
            } catch { /* 壊れた1件だけ配置復元を省略し、対象へのジャンプは続ける */ }
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
            await OpenEditorSupportAsync(tab);
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
        _trailLastPane = pane;   // 戻った先を「直近のペイン」として同期する
        _trailLastPaneMode = entry.Mode;
    }
    private void JumpToPanel(TrailEntryViewModel entry) {
        if (!Enum.TryParse<SidebarPanel>(entry.Target, out var panel))
            return;
        _vm.ActivePanel = panel;
        _vm.IsSidebarVisible = true;
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
