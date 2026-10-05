namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナルの「最近のコマンド」（§24.20）。可視ターミナルで人間が実行し終えた
/// コマンドを終了コード付きでワークスペースへ残し、コマンドパレットから選び直して<b>ターミナルへ送る</b>
/// （§24.3 の「送る」＝プロンプトへ入力するだけで実行はしない。複数行はプロンプト暴発を避けてコンポーザへ）。
/// 記録は UI スレッドでスナップショットの参照を差し替えるだけで、ディスクへの書き出しは定期保存の
/// 書き出しスレッドが引き取る（§31.15）。</summary>
public partial class ShellWindow {
    private readonly Dictionary<Guid, TerminalCommandRunTracker> _terminalRunTrackers = new();
    /// <summary>裏のワークスペースのタブで終わった実行。スナップショットは表のものしか書き出さないので、
    /// そのワークスペースへ戻ったときに積む（<see cref="ApplyPendingTerminalCommands"/>）。</summary>
    private readonly Dictionary<Guid, List<RecentTerminalCommandRun>> _pendingTerminalCommands = new();

    private void HookTerminalRecentCommands(TerminalTab tab) {
        var tracker = new TerminalCommandRunTracker();
        _terminalRunTrackers[tab.Id] = tracker;
        tab.View.CommandHistoryRecorded += (_, command) => tracker.OnCommandLine(command);
        tab.View.ShellCommandActivity += (_, e) => {
            // C／D に載るコマンド行（Terminal 1.0.37〜）を渡す——直前と同じコマンドの連続実行も毎回記録できる。
            switch (e.Phase) {
                case ShellCommandPhase.PromptStart:
                    tracker.OnPromptStart();
                    break;
                case ShellCommandPhase.CommandExecuted:
                    tracker.OnExecuted(e.CommandLine);
                    break;
                case ShellCommandPhase.CommandDone:
                    if (tracker.OnDone(e.ExitCode, DateTime.UtcNow, e.CommandLine) is { } run)
                        RecordTerminalCommandRun(tab.Id, run);
                    break;
            }
        };
        HookTerminalCommandOutputs(tab);
    }

    private void RecordTerminalCommandRun(Guid tabId, RecentTerminalCommandRun run) {
        var owner = _terminalWorkspaces.FirstOrDefault(pair => pair.Value.Tabs.Any(t => t.Id == tabId)).Key;
        if (owner == Guid.Empty)
            return;   // どのワークスペースにも属さない仮のタブ（ワークスペース未選択時）は残さない
        if (_activeWorkspace is { } active && active.Id == owner) {
            if (RecentTerminalCommands.Record(active.RecentTerminalCommands, run) is { } updated) {
                active.RecentTerminalCommands = updated;
                SaveActiveWorkspaceSnapshot();
            }
            return;
        }
        if (!_pendingTerminalCommands.TryGetValue(owner, out var pending))
            _pendingTerminalCommands[owner] = pending = new();
        pending.Add(run);
        if (pending.Count > RecentTerminalCommands.MaxCount)
            pending.RemoveAt(0);
    }

    /// <summary>復元したワークスペースへ、裏で終わっていた実行を積む（保存は復元の最後の定期保存に乗る）。</summary>
    private void ApplyPendingTerminalCommands(WorkspaceSnapshot workspace) {
        if (!_pendingTerminalCommands.Remove(workspace.Id, out var pending))
            return;
        if (RecentTerminalCommands.RecordAll(workspace.RecentTerminalCommands, pending) is { } updated)
            workspace.RecentTerminalCommands = updated;
    }

    private void ForgetTerminalRecentCommands(Guid tabId) => _terminalRunTrackers.Remove(tabId);

    /// <summary>パレットに並べる「最近のコマンド」（新しい順）。</summary>
    private List<PaletteCommand> BuildRecentTerminalPaletteCommands() {
        var items = _activeWorkspace?.RecentTerminalCommands;
        if (items is null || items.Count == 0)
            return new();
        var now = DateTime.UtcNow;
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Command))
            .Select(item => {
                var command = item.Command;
                return new PaletteCommand(RecentTerminalCommandCategory, RecentTerminalCommands.Title(command),
                    () => SendRecentCommandToTerminal(command)) {
                    Badge = RecentTerminalCommands.Badge(item.ExitCode),
                    Detail = RecentTerminalCommands.Detail(item, now),
                };
            })
            .ToList();
    }

    private const string RecentTerminalCommandCategory = "最近のコマンド";

    /// <summary>「最近のコマンドを選び直す」。一覧を最近のコマンドだけにしてパレットを開く。</summary>
    private void OpenRecentTerminalCommands()
        => OpenCommandPalette(PaletteMode.Command, BuildRecentTerminalPaletteCommands());

    /// <summary>選んだコマンドを可視ターミナルのプロンプトへ入力する（Enter は送らない）。
    /// ペグボードの「ターミナルへ送る」と同じ流儀（§24.3）。</summary>
    private void SendRecentCommandToTerminal(string command) {
        if (RecentTerminalCommands.IsMultiline(command)) {
            InsertIntoComposer(command);
            return;
        }
        SetPaneVisible(PaneKind.Terminal, true);
        _activeTerminalTab?.View.SendTerminalInput(command);
        FocusPane(PaneKind.Terminal);
    }
}
