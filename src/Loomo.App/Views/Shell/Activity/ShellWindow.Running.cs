using System.Windows.Threading;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 動いているもの面（§24.54）。部屋の中でいま走っているもの——ターミナルの前面のコマンド、
/// Claude Code、デバッグ・実行のセッション、IDE のビルド・テスト、AI ペイン、言語サーバー——を 1 か所に集め、
/// 行を押せばその出所へ行き、■ で止める。材料は袖のバッジ（§24.1）と TABS の要約（§24.53）と同じもので、
/// 新しく見張るものは無い。引き直しはまとめて 1 回にする（合図は短い間に何度も来る）。</summary>
public partial class ShellWindow {
    private readonly Dictionary<string, (Action Activate, Action? Stop)> _runningActions = new(StringComparer.Ordinal);
    private bool _runningRefreshQueued;

    private void InitializeRunningPanel() {
        _vm.Running.ActivateRequested += (_, item) => {
            if (_runningActions.TryGetValue(item.Key, out var actions)) actions.Activate();
        };
        _vm.Running.StopRequested += (_, item) => {
            if (_runningActions.TryGetValue(item.Key, out var actions)) actions.Stop?.Invoke();
        };
        // 「あとで」（§24.71）で人の番の数が変わったら、ActivityBar の印も付け直す。
        _vm.Running.AttentionChanged += (_, _) => UpdateRunningActivityMark();
        TerminalStatusChanged += RequestRunningRefresh;
        TerminalStatusTicked += now => _vm.Running.Tick(now);
        // 言語サーバーの状態は裏のスレッドから届く。
        _lspWorkspace.ServerStateChanged += () => Dispatcher.BeginInvoke(RequestRunningRefresh);
        foreach (var manager in new DebugManagerViewModelBase[] { _vm.Debug, _vm.TsIde }) {
            manager.Sessions.CollectionChanged += (_, _) => RequestRunningRefresh();
            manager.SessionStateChanged += RequestRunningRefresh;
            manager.PropertyChanged += (_, e) => {
                if (e.PropertyName is nameof(DebugManagerViewModelBase.IsTaskRunning)
                    or nameof(DebugManagerViewModelBase.StatusMessage))
                    RequestRunningRefresh();
            };
        }
        _vm.AiBar.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(AiBarViewModel.IsBusy) or nameof(AiBarViewModel.StatusText))
                RequestRunningRefresh();
        };
        RequestRunningRefresh();
    }

    /// <summary>引き直しを予約する（すでに予約があれば何もしない）。</summary>
    private void RequestRunningRefresh() {
        if (_runningRefreshQueued)
            return;
        _runningRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshRunningPanel);
    }

    private void RefreshRunningPanel() {
        _runningRefreshQueued = false;
        _runningActions.Clear();

        var agents = _paneActivity.AgentTabs().ToDictionary(a => a.TabId, a => a.State);
        var terminals = new List<RunningTerminalSource>();
        foreach (var tab in _terminalTabs) {
            var id = tab.Id;
            var agent = agents.TryGetValue(id, out var state) ? state : (ClaudeAgentState?)null;
            var status = TerminalStatusOf(id);
            var programs = _paneActivity.ProgramsOf(id);
            if (agent is null && !status.IsRunning && programs.Count == 0)
                continue;
            terminals.Add(new RunningTerminalSource(id, TerminalTabTitle(id) ?? "Terminal", status, agent, programs));
            var view = tab.View;
            Action go = () => GoToTerminalTab(id);
            Action stop = () => view.SendTerminalInput("\u0003");
            _runningActions[$"terminal:{id}"] = (go, stop);
            _runningActions[$"claude:{id}"] = (go, stop);
            foreach (var program in programs)
                _runningActions[$"program:{id}:{program.Id}"] = (go, stop);
        }

        var sessions = new List<RunningDebugSource>();
        var ideTasks = new List<RunningIdeTaskSource>();
        foreach (var (kind, manager, pane) in new (PaneKind, DebugManagerViewModelBase, string)[] {
                     (PaneKind.Debug, _vm.Debug, "IDE"), (PaneKind.TsIde, _vm.TsIde, "TS IDE") }) {
            foreach (var session in manager.Sessions) {
                sessions.Add(new RunningDebugSource(pane, session.SessionId, session.DisplayName,
                    session.IsBusy, session.IsStopped, session.StatusMessage));
                var target = session;
                _runningActions[$"debug:{session.SessionId}"] = (
                    () => { manager.ActiveSession = target; ShowPaneForRunning(kind); },
                    () => _ = StopDebugSessionAsync(target));
            }
            if (manager.IsTaskRunning) {
                ideTasks.Add(new RunningIdeTaskSource(pane, manager.StatusMessage));
                _runningActions[$"idetask:{pane}"] = (() => ShowPaneForRunning(kind), null);
            }
        }

        _runningActions["ai"] = (() => ShowPaneForRunning(PaneKind.Ai), null);
        var servers = _lspWorkspace.ServerStatuses;
        foreach (var server in servers)
            _runningActions[$"lsp:{server.Executable}|{server.Root}"] = (OpenLspSettingsForRunning, null);

        var entries = RunningEntries.Build(terminals, sessions, ideTasks, servers,
            _vm.AiBar.IsBusy, _vm.AiBar.StatusText);
        _vm.Running.Apply(entries, DateTime.UtcNow);
        UpdateRunningActivityMark();
    }

    private void ShowPaneForRunning(PaneKind kind) {
        EnsurePaneVisibleOrSwapTopLeft(kind);
        FocusPane(kind);
    }

    private void OpenLspSettingsForRunning() {
        _vm.SettingsCategory = SettingsCategory.Lsp;
        _vm.IsSettingsOverlayOpen = true;
    }

    /// <summary>IDE ペインの「停止」と同じ止め方（セッションを取り消してからアダプタへ止めを送る）。</summary>
    private static async Task StopDebugSessionAsync(DebugSessionViewModel session) {
        ((IDebugSession)session).CancelSession();
        try { await session.DebugService.StopAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { }
    }

    /// <summary>ActivityBar のアイコンに、人の番の数を添える（面を開いていなくても気づけるように）。</summary>
    private void UpdateRunningActivityMark() {
        if (_vm.ActivityBar.ItemFor(SidebarPanel.Running) is { } item)
            item.Badge = _vm.Running.AttentionCount > 0 ? _vm.Running.AttentionCount.ToString() : null;
    }
}
