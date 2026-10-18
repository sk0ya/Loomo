using System.Windows.Threading;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ターミナルタブごとのいまの様子（§24.53）。場所・ブランチ・前面のコマンド・最後に終わったコマンドを
/// タブ ID ごとに持ち、TABS の行の 2 行目へ流す。動いているもの面（§24.54）も同じ表を読む。
/// <para>材料はシェル統合の合図だけ：OSC 133 の A（プロンプト＝cwd が確定した）・C（走り始め）・D（終わり）と、
/// OSC 633;E（コマンド行）。ブランチは cwd から <c>.git/HEAD</c> を裏で読む（git は起こさない）。</para></summary>
public partial class ShellWindow {
    private readonly Dictionary<Guid, TerminalTabStatus> _terminalStatus = new();
    private DispatcherTimer? _terminalStatusTimer;

    /// <summary>あるタブの様子が変わった（動いているもの面が引き直す合図）。</summary>
    private event Action? TerminalStatusChanged;

    private void HookTerminalStatus(TerminalTab tab) {
        var id = tab.Id;
        string? pendingCommand = null;
        tab.View.CommandHistoryRecorded += (_, command) => pendingCommand = RecentTerminalCommands.Normalize(command);
        tab.View.ShellCommandActivity += (_, e) => {
            var current = TerminalStatusOf(id);
            switch (e.Phase) {
                case ShellCommandPhase.PromptStart:
                    pendingCommand = null;
                    // 走っている途中でプロンプトが来た＝D を取りこぼした。走っていない扱いへ戻す。
                    UpdateTerminalStatus(id, current with { RunningCommand = null, RunningSinceUtc = null });
                    RefreshTerminalLocation(tab);
                    break;
                case ShellCommandPhase.CommandExecuted:
                    UpdateTerminalStatus(id, current with {
                        RunningCommand = RecentTerminalCommands.Normalize(e.CommandLine) ?? pendingCommand,
                        RunningSinceUtc = DateTime.UtcNow,
                    });
                    break;
                case ShellCommandPhase.CommandDone when current.IsRunning:
                    var command = RecentTerminalCommands.Normalize(e.CommandLine) ?? current.RunningCommand;
                    UpdateTerminalStatus(id, current with {
                        RunningCommand = null,
                        RunningSinceUtc = null,
                        LastRun = command is null ? current.LastRun
                            : new RecentTerminalCommandRun(command, e.ExitCode, DateTime.UtcNow),
                    });
                    pendingCommand = null;
                    break;
            }
        };
        RefreshTerminalLocation(tab);
    }

    private TerminalTabStatus TerminalStatusOf(Guid id)
        => _terminalStatus.TryGetValue(id, out var status) ? status : TerminalTabStatus.Empty;

    private void UpdateTerminalStatus(Guid id, TerminalTabStatus status) {
        if (_terminalStatus.TryGetValue(id, out var old) && old == status)
            return;
        _terminalStatus[id] = status;
        _vm.Tabs.SetTerminalStatus(id, status, _workspace.Folders, DateTime.UtcNow);
        EnsureTerminalStatusTimer();
        TerminalStatusChanged?.Invoke();
    }

    /// <summary>cwd を読み直し、ブランチは裏で <c>.git/HEAD</c> から。プロンプトのたびに呼ぶ
    /// （<c>git checkout</c> の直後もプロンプトで拾える）。</summary>
    private void RefreshTerminalLocation(TerminalTab tab) {
        var id = tab.Id;
        var cwd = tab.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd))
            return;
        UpdateTerminalStatus(id, TerminalStatusOf(id) with { WorkingDirectory = cwd });
        _ = Task.Run(() => GitHeadReader.Read(cwd)).ContinueWith(t => {
            if (t.IsFaulted || !_terminalStatus.ContainsKey(id))
                return;
            var now = TerminalStatusOf(id);
            if (string.Equals(now.WorkingDirectory, cwd, StringComparison.OrdinalIgnoreCase))
                UpdateTerminalStatus(id, now with { Head = t.Result });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ForgetTerminalStatus(Guid id) {
        if (_terminalStatus.Remove(id))
            TerminalStatusChanged?.Invoke();
    }

    /// <summary>ワークスペースを切り替えると TABS の行は作り直されるので、覚えている様子を新しい行へ入れ直す。</summary>
    private void WireTerminalStatusRows() {
        _vm.Tabs.TerminalTabs.CollectionChanged += (_, e) => {
            if (e.NewItems is null)
                return;
            var now = DateTime.UtcNow;
            foreach (TabEntryViewModel row in e.NewItems) {
                if (_terminalStatus.TryGetValue(row.Id, out var status))
                    _vm.Tabs.SetTerminalStatus(row.Id, status, _workspace.Folders, now);
            }
        };
    }

    /// <summary>「3分前」「1分23秒」を進める時計。走っているタブがあれば 1 秒ごと、無ければ 30 秒ごと。
    /// 何も分かっていないうちは回さない。</summary>
    private void EnsureTerminalStatusTimer() {
        var interval = _terminalStatus.Values.Any(s => s.IsRunning) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
        if (_terminalStatusTimer is null) {
            _terminalStatusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            _terminalStatusTimer.Tick += (_, _) => TickTerminalStatus();
            _terminalStatusTimer.Start();
            return;
        }
        if (_terminalStatusTimer.Interval != interval)
            _terminalStatusTimer.Interval = interval;
    }

    private void TickTerminalStatus() {
        var now = DateTime.UtcNow;
        foreach (var (id, status) in _terminalStatus)
            _vm.Tabs.SetTerminalStatus(id, status, _workspace.Folders, now);
        TerminalStatusTicked?.Invoke(now);
        EnsureTerminalStatusTimer();
    }

    /// <summary>時計が進んだ（経過時間だけを引き直す合図）。</summary>
    private event Action<DateTime>? TerminalStatusTicked;
}
