using Terminal.Sessions;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナルの常駐セッション（§34）。シェルは常駐ホストが持ち、タブ ID がその鍵。
/// ここは「どのタブを常駐させるか」「スナップショットへ何を書くか・どう立て直すか」「いつ殺すか」の配線。</summary>
public partial class ShellWindow {
    private bool PersistentTerminalsEnabled => _settings.PersistentTerminalSessions && _ptyHost.IsAvailable;

    /// <summary>タブのシェルの起こし方を決める。常駐させるなら常駐ホストで起こし（あれば繋ぎ直す）、どちらでも
    /// Loomo の環境変数（タブ ID・Claude Code の IDE 連携とフックの宛先、§38）を足す。ワークスペースを開く前の
    /// 仮のタブは持ち主がおらず、次回どこにも戻らないので常駐させない。</summary>
    private void ConfigureTerminalSession(TerminalTabView view, Guid tabId, Guid? workspaceId) {
        var owner = workspaceId is { } id && PersistentTerminalsEnabled ? id : (Guid?)null;
        var environment = TerminalEnvironmentFor(tabId);
        view.SessionFactory = original => {
            var request = WithEnvironment(original, environment);
            if (owner is null)
                return RememberShell(tabId, new ConPtySession(request.Columns, request.Rows, request.LaunchCommandLine,
                    request.WorkingDirectory, request.EnvironmentVariables));
            try {
                var session = _ptyHost.OpenSession(tabId, owner.Value, request);
                _localTerminalTabs.TryRemove(tabId, out _);
                _remoteTerminalSessions[tabId] = session;   // 閉じるときに預ける相手（§24.41）
                return RememberShell(tabId, session);
            } catch (Exception ex) {
                // 同じシェルは先勝ち（§34.5）：別の Loomo が繋いでいるなら奪わない。ホストが起きない・答えない
                // ときも端末が使えなくなるよりはよい。どちらもこのタブには常駐しない新しいシェルを立て、
                // 閉じても同じ ID の向こうのシェルを殺さないよう覚えておく。
                if (ex is not sk0ya.Loomo.Services.Terminal.PtySessionInUseException)
                    System.Diagnostics.Trace.WriteLine($"端末の常駐ホストを使えないため自前のシェルで開きます: {ex.Message}");
                _localTerminalTabs[tabId] = 0;
                return RememberShell(tabId, new ConPtySession(request.Columns, request.Rows, request.LaunchCommandLine,
                    request.WorkingDirectory, request.EnvironmentVariables));
            }
        };
    }

    /// <summary>タブ ID → いまのシェルの接続。シェルの PID を引くためだけに持つ（§39.4.2）。端末ビューの
    /// バックグラウンドスレッドから書かれる。タブを閉じたら外す。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ITerminalSession> _terminalShells = new();

    private ITerminalSession RememberShell(Guid tabId, ITerminalSession session) {
        _terminalShells[tabId] = session;
        return session;
    }

    /// <summary>
    /// タブのシェルの PID（分からなければ null）。自前の ConPTY なら起動したプロセス、常駐ならホストが開いたときに返したもの。
    /// タブの中で動いている Claude Code がこのタブのものかを、親をたどって確かめるのに使う——同じ場所で claude が
    /// 二つ動いていても、打った時刻に頼らずに決まる。
    /// </summary>
    private int? TerminalShellProcessId(Guid tabId)
        => _terminalShells.TryGetValue(tabId, out var session)
            ? session switch {
                ConPtySession local when local.ProcessId > 0 => local.ProcessId,
                sk0ya.Loomo.Services.Terminal.RemotePtySession { ShellProcessId: > 0 } remote => remote.ShellProcessId,
                _ => null,
            }
            : null;

    /// <summary>先勝ちに負けて自前のシェルで立てたタブ。端末ビューのバックグラウンドスレッドから書かれる。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _localTerminalTabs = new();

    /// <summary>常駐ホストのシェルへの接続（タブ ID → 接続）。端末ビューのバックグラウンドスレッドから書かれる。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, sk0ya.Loomo.Services.Terminal.RemotePtySession>
        _remoteTerminalSessions = new();

    /// <summary>手で閉じたターミナル（§24.41）。シェルはホストが期限まで預かっている。</summary>
    private readonly ClosedTerminalTabs _closedTerminalTabs = new();

    /// <summary>
    /// 手で閉じるタブのシェルを、殺さずにホストへ預ける（§24.41）。預けたら true（呼び手は殺さない）。
    /// 常駐でないタブ・まだ一度も繋いでいないタブ（復元しただけで表示していない）は預けない——繋がっていない
    /// シェルは中身を見ていないので、戻す価値より「閉じたのに残る」害の方が大きい。
    /// </summary>
    private async Task<bool> TryParkTerminalAsync(TerminalTab tab) {
        if (!PersistentTerminalsEnabled || CurrentTerminalWorkspace.WorkspaceId is not { } workspaceId
            || !_remoteTerminalSessions.TryRemove(tab.Id, out var session))
            return false;
        var entry = new ClosedTerminalTab(tab.Id, workspaceId, tab.CustomName, tab.View.WorkingDirectory, DateTime.UtcNow, tab.ColorKey, tab.Note);
        // Park はホストが読み終えるまで待つ（ふつうは数 ms）。UI スレッドでは待たない。
        await Task.Run(() => session.Park(ClosedTerminalTabs.KeepFor));
        _closedTerminalTabs.Push(entry);
        return true;
    }

    /// <summary>このワークスペースで最後に閉じたターミナルを、同じタブ ID で開き直す（§24.41）。ホストが預かっていた
    /// シェルへ繋ぎ直すので、画面・スクロールバック・動いていたプロセスごと戻る。</summary>
    private void ReopenClosedTerminalTab() {
        if (CurrentTerminalWorkspace.WorkspaceId is not { } workspaceId
            || _closedTerminalTabs.TakeLatest(workspaceId, DateTime.UtcNow) is not { } closed) {
            ShowRefactorStatus("開き直せるターミナルはありません（閉じてから 3 分たったものは戻せません）。");
            return;
        }
        var cwd = Directory.Exists(closed.WorkingDirectory) ? closed.WorkingDirectory!
            : _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
        AddRestoredTerminalTab(cwd, closed.Id, closed.CustomName, closed.ColorKey, closed.Note);
        if (!IsPaneVisible(PaneKind.Terminal))
            SetPaneVisible(PaneKind.Terminal, true);
        ActivateTerminalTab(closed.Id);
        FocusPane(PaneKind.Terminal);
    }

    private int ClosedTerminalCount()
        => CurrentTerminalWorkspace.WorkspaceId is { } id ? _closedTerminalTabs.Count(id, DateTime.UtcNow) : 0;

    /// <summary>今のワークスペースのターミナルタブをスナップショットへ書く。切替の途中などで、いま見えている
    /// タブ集合がこのスナップショットの持ち物でないときは触らない（前に書いた内容を残す）。</summary>
    private void CaptureTerminalTabs(WorkspaceSnapshot snapshot) {
        if (CurrentTerminalWorkspace.WorkspaceId != snapshot.Id)
            return;
        snapshot.TerminalTabs = _terminalTabs.Select(tab => new TerminalTabSnapshot {
            Id = tab.Id, CustomName = tab.CustomName, WorkingDirectory = tab.View.WorkingDirectory, ColorKey = tab.ColorKey,
            Note = tab.Note,
        }).ToList();
        snapshot.ActiveTerminalTabId = _activeTerminalTab?.Id;
        snapshot.TerminalViewLayout = _terminalViews?.Capture();
    }

    /// <summary>
    /// 復元の前に、常駐ホストに残っているシェルの ID を聞いておく。聞く必要が無い（このワークスペースのタブを
    /// もう立ててある・常駐が切ってある・前回のタブが無い）か、ホストが答えなければ null。
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>?> QueryRestorableTerminalSessionsAsync(WorkspaceSnapshot workspace) {
        if (!PersistentTerminalsEnabled || workspace.TerminalTabs.Count == 0 ||
            _terminalWorkspaces.TryGetValue(workspace.Id, out var existing) && existing.IsInitialized)
            return null;
        try {
            var sessions = await _ptyHost.TryListAsync();
            return sessions?.Select(s => s.SessionId).ToHashSet();
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>前回のタブを同じ ID で立て直す。立てたら true。シェルへの接続はタブが表示されたとき
    /// （端末ビューの Loaded）に起きるので、ここで待つものは無い。常駐ホストにシェルが残っていないタブは
    /// 戻せないので立て直さず、スナップショットからも消える（次の保存で）。</summary>
    private bool RestorePersistentTerminalTabs(
        WorkspaceSnapshot workspace, TerminalWorkspaceTabs terminalWorkspace, string fallbackDirectory,
        IReadOnlyCollection<Guid>? hostSessions) {
        if (!PersistentTerminalsEnabled) {
            // 常駐を切ったなら、前に常駐させたシェルはもうどのタブにも戻らない。残すと見えないまま生き続ける。
            if (_ptyHost.IsAvailable && terminalWorkspace.WorkspaceId is { } owner)
                _ = _ptyHost.KillWorkspaceAsync(owner);
            return false;
        }
        _ = AdoptOrphanTerminalSessionsAsync(terminalWorkspace);
        var restorable = WorkspaceSessionCoordinator.ResolveRestorableTerminalTabs(workspace.TerminalTabs, hostSessions);
        if (restorable.Count == 0)
            return false;
        foreach (var saved in restorable) {
            var cwd = Directory.Exists(saved.WorkingDirectory) ? saved.WorkingDirectory! : fallbackDirectory;
            AddRestoredTerminalTab(cwd, saved.Id, saved.CustomName, saved.ColorKey, saved.Note);
        }
        terminalWorkspace.NextTabNumber = _terminalTabs.Count + 1;
        var active = workspace.ActiveTerminalTabId is { } id && _terminalTabs.Any(t => t.Id == id)
            ? id : _terminalTabs[0].Id;
        ActivateTerminalTab(active, focusView: false);
        _terminalViews?.Restore(workspace.TerminalViewLayout, _terminalTabs.Select(t => t.Id));
        return true;
    }

    /// <summary>
    /// ホストには生きているのに、スナップショットにタブが無いシェルを拾う。保存の前に Loomo が落ちると
    /// こうなる——タブを閉じたなら殺してあるので、残っているのは失ってはいけないものだけ（§34.6）。
    /// </summary>
    private async Task AdoptOrphanTerminalSessionsAsync(TerminalWorkspaceTabs terminalWorkspace) {
        if (terminalWorkspace.WorkspaceId is not { } workspaceId)
            return;
        IReadOnlyList<sk0ya.Loomo.Core.Pty.PtyProtocol.SessionInfo> sessions;
        try {
            sessions = await _ptyHost.ListAsync();
        } catch (Exception) {
            return;
        }
        // 待っている間にワークスペースが切り替わっていたら、拾ったタブは別のワークスペースへ紛れ込む。
        if (!ReferenceEquals(_activeTerminalWorkspace, terminalWorkspace))
            return;
        var orphans = sessions
            // 閉じたタブとして預かっているシェル（§24.41）は持ち主のいないシェルではない——拾うと閉じたタブが勝手に戻る。
            .Where(s => s.WorkspaceId == workspaceId && !s.Attached && s.ParkedUntilUtc is null
                && terminalWorkspace.Tabs.All(t => t.Id != s.SessionId))
            .OrderBy(s => s.CreatedUtc)
            .ToList();
        if (orphans.Count == 0)
            return;
        foreach (var orphan in orphans) {
            var cwd = Directory.Exists(orphan.WorkingDirectory)
                ? orphan.WorkingDirectory!
                : _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
            AddRestoredTerminalTab(cwd, orphan.SessionId, customName: null);
        }
        SaveActiveWorkspaceSnapshot();
    }

    private void AddRestoredTerminalTab(string cwd, Guid id, string? customName, string? colorKey = null, string? note = null) {
        var tab = CreateTerminalTab(cwd, id);
        tab.CustomName = customName;
        tab.ColorKey = TerminalTabColors.Normalize(colorKey);
        tab.Note = TabsViewModel.NormalizeNote(note);
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false, customName, tab.ColorKey, tab.Note);
    }

    /// <summary>
    /// 閉じたタブのシェルを確実に殺す。表示されたことのあるタブはビューの Dispose で殺しているが、
    /// 復元しただけで一度も表示していないタブはまだ繋がっておらず、ビューからは届かない——放っておくと
    /// 次の復元で「拾う」側に回って生き返る。
    /// </summary>
    private void KillPersistentTerminal(Guid tabId) {
        // 設定は今の値で、このタブが常駐で作られたかとは限らないので見ない（居なければ何も起きない）。
        // 先勝ちに負けたタブの同じ ID のシェルは、別の Loomo の持ち物。
        if (_localTerminalTabs.TryRemove(tabId, out _))
            return;
        if (_ptyHost.IsAvailable)
            _ = _ptyHost.KillAsync(tabId);
    }
}
