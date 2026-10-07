using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナルの常駐セッション（§34）。シェルは常駐ホストが持ち、タブ ID がその鍵。
/// ここは「どのタブを常駐させるか」「スナップショットへ何を書くか・どう立て直すか」「いつ殺すか」の配線。</summary>
public partial class ShellWindow {
    private bool PersistentTerminalsEnabled => _settings.PersistentTerminalSessions && _ptyHost.IsAvailable;

    /// <summary>タブのシェルを常駐ホストで起こす（あれば繋ぎ直す）ようにする。ワークスペースを開く前の
    /// 仮のタブは持ち主がおらず、次回どこにも戻らないので常駐させない。</summary>
    private void ConfigurePersistentTerminal(TerminalTabView view, Guid tabId, Guid? workspaceId) {
        if (workspaceId is not { } owner || !PersistentTerminalsEnabled)
            return;
        view.SessionFactory = request => _ptyHost.OpenSession(tabId, owner, request);
    }

    /// <summary>今のワークスペースのターミナルタブをスナップショットへ書く。切替の途中などで、いま見えている
    /// タブ集合がこのスナップショットの持ち物でないときは触らない（前に書いた内容を残す）。</summary>
    private void CaptureTerminalTabs(WorkspaceSnapshot snapshot) {
        if (CurrentTerminalWorkspace.WorkspaceId != snapshot.Id)
            return;
        snapshot.TerminalTabs = _terminalTabs.Select(tab => new TerminalTabSnapshot {
            Id = tab.Id, CustomName = tab.CustomName, WorkingDirectory = tab.View.WorkingDirectory,
        }).ToList();
        snapshot.ActiveTerminalTabId = _activeTerminalTab?.Id;
        snapshot.TerminalViewLayout = _terminalViews?.Capture();
    }

    /// <summary>前回のタブを同じ ID で立て直す。立てたら true。シェルへの接続はタブが表示されたとき
    /// （端末ビューの Loaded）に起きるので、ここで待つものは無い。</summary>
    private bool RestorePersistentTerminalTabs(
        WorkspaceSnapshot workspace, TerminalWorkspaceTabs terminalWorkspace, string fallbackDirectory) {
        if (!PersistentTerminalsEnabled) {
            // 常駐を切ったなら、前に常駐させたシェルはもうどのタブにも戻らない。残すと見えないまま生き続ける。
            if (_ptyHost.IsAvailable && terminalWorkspace.WorkspaceId is { } owner)
                _ = _ptyHost.KillWorkspaceAsync(owner);
            return false;
        }
        _ = AdoptOrphanTerminalSessionsAsync(terminalWorkspace);
        if (workspace.TerminalTabs.Count == 0)
            return false;
        foreach (var saved in workspace.TerminalTabs) {
            var cwd = Directory.Exists(saved.WorkingDirectory) ? saved.WorkingDirectory! : fallbackDirectory;
            AddRestoredTerminalTab(cwd, saved.Id, saved.CustomName);
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
            .Where(s => s.WorkspaceId == workspaceId && !s.Attached && terminalWorkspace.Tabs.All(t => t.Id != s.SessionId))
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

    private void AddRestoredTerminalTab(string cwd, Guid id, string? customName) {
        var tab = CreateTerminalTab(cwd, id);
        tab.CustomName = customName;
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false, customName);
    }

    /// <summary>
    /// 閉じたタブのシェルを確実に殺す。表示されたことのあるタブはビューの Dispose で殺しているが、
    /// 復元しただけで一度も表示していないタブはまだ繋がっておらず、ビューからは届かない——放っておくと
    /// 次の復元で「拾う」側に回って生き返る。
    /// </summary>
    private void KillPersistentTerminal(Guid tabId) {
        // 設定は今の値で、このタブが常駐で作られたかとは限らないので見ない（居なければ何も起きない）。
        if (_ptyHost.IsAvailable)
            _ = _ptyHost.KillAsync(tabId);
    }
}
