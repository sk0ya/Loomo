namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ワークスペース復元時のタブ実体の付け替え（端末／エディタ／ブラウザの Restore・Attach・Detach・GetOrCreate）。切替の入口とスナップショット保存は ShellWindow.Workspaces.cs。</summary>
public partial class ShellWindow {
    private void RestoreTerminalTabs(
        WorkspaceSnapshot workspace, WorkspaceSwitchProfiler? profile = null) {
        var terminalWorkspace = WorkspaceSessionCoordinator.GetOrCreateWorkspace(
            _terminalWorkspaces, workspace.Id, static () => new TerminalWorkspaceTabs());
        _activeTerminalWorkspace = terminalWorkspace;
        _terminalTabs = terminalWorkspace.Tabs;
        if (terminalWorkspace.IsInitialized && _terminalTabs.Count > 0) {
            AttachTerminalTabs();
            profile?.Lap("terminal.attach");
            ActivateTerminalTab(
                terminalWorkspace.ActiveTabId ?? _terminalTabs[0].Id, profile, focusView: false);
            _terminalViews?.Restore(workspace.TerminalViewLayout, _terminalTabs.Select(t => t.Id));
            return;
        }
        terminalWorkspace.IsInitialized = true;
        var restore = WorkspaceSessionCoordinator.ResolveTerminalTabRestorePlan(workspace);
        foreach (var snapshot in restore.Snapshots) {
            var cwd = WorkspaceSessionCoordinator.ResolveWorkingDirectory(
                snapshot.WorkingDirectory, workspace.RootPath)!;
            var tab = CreateTerminalTab(cwd, snapshot.Id == Guid.Empty ? null : snapshot.Id);
            _terminalTabs.Add(tab);
            _vm.Tabs.AddTerminalTab(tab.Id, snapshot.Title ?? tab.View.HeaderTitle, false);
        }
        ActivateTerminalTab(_terminalTabs[restore.ActiveIndex].Id, focusView: false);
        _terminalViews?.Restore(workspace.TerminalViewLayout, _terminalTabs.Select(t => t.Id));
    }
    private void RestoreEditorTabs(
        WorkspaceSnapshot workspace, WorkspaceSwitchProfiler? profile = null) {
        var editorWorkspace = WorkspaceSessionCoordinator.GetOrCreateWorkspace(
            _editorWorkspaces, workspace.Id, static () => new EditorWorkspaceTabs());
        _activeEditorWorkspace = editorWorkspace;
        _editorTabs = editorWorkspace.Tabs;
        if (editorWorkspace.IsInitialized && _editorTabs.Count > 0) {
            AttachEditorTabs();
            profile?.Lap("editor.attach");
            ActivateEditorTab(
                editorWorkspace.ActiveTabId ?? _editorTabs[0].Id, profile, focusView: false);
            _editorViews?.Restore(workspace.EditorViewLayout, _editorTabs.Select(t => t.Id));
            return;
        }
        editorWorkspace.IsInitialized = true;
        var restore = WorkspaceSessionCoordinator.ResolveEditorTabRestorePlan(workspace);
        foreach (var snapshot in restore.Snapshots) {
            var tab = CreatePendingEditorTab(snapshot);
            _editorTabs.Add(tab);
            _vm.Tabs.AddEditorTab(tab.Id, snapshot.FilePath, snapshot.IsModified, false);
        }
        ActivateEditorTab(_editorTabs[restore.ActiveIndex].Id, focusView: false);
        _editorViews?.Restore(workspace.EditorViewLayout, _editorTabs.Select(t => t.Id));
    }
    private void DetachTerminalTabs() {
        CurrentTerminalWorkspace.ActiveTabId = _activeTerminalTab?.Id;
        _terminalViews?.Reset();
        _vm.Tabs.TerminalTabs.Clear();
        _activeTerminalTab = null;
    }
    private void AttachTerminalTabs() {
        _terminalViews?.Reset();
        _vm.Tabs.TerminalTabs.Clear();
        foreach (var tab in _terminalTabs)
            _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false);
    }
    private void DetachEditorTabs() {
        ResetEditorSupportForWorkspaceSwitch();
        CurrentEditorWorkspace.ActiveTabId = _activeEditorTab?.Id;
        _editorViews?.Reset();
        _vm.Tabs.EditorTabs.Clear();
        _activeEditorTab = null;
        _previewEditorTab = null;
    }
    private void AttachEditorTabs() {
        _editorViews?.Reset();
        _vm.Tabs.EditorTabs.Clear();
        foreach (var tab in _editorTabs)
            _vm.Tabs.AddEditorTab(tab.Id, tab.PeekFilePath, tab.PeekIsModified, false);
    }
    private async Task RestoreBrowserTabsAsync(WorkspaceSnapshot workspace) {
        var browserWorkspace = WorkspaceSessionCoordinator.GetOrCreateWorkspace(
            _browserWorkspaces, workspace.Id, static () => new BrowserWorkspaceTabs());
        _activeBrowserWorkspace = browserWorkspace;
        _browserTabs = browserWorkspace.Tabs;
        if (browserWorkspace.IsInitialized && _browserTabs.Count > 0) {
            await AttachBrowserTabsAsync();
            ActivateBrowserTab(browserWorkspace.ActiveTabId ?? _browserTabs[0].Id);
            return;
        }
        browserWorkspace.IsInitialized = true;
        var restore = WorkspaceSessionCoordinator.ResolveBrowserTabRestorePlan(workspace, DefaultBrowserUrl);
        foreach (var snapshot in restore.Snapshots)
            CreateBrowserTab(snapshot.Url ?? DefaultBrowserUrl,
                snapshot.Id == Guid.Empty ? null : snapshot.Id, snapshot.Title);
        ActivateBrowserTab(_browserTabs[restore.ActiveIndex].Id);
    }
    private void DetachBrowserTabs() {
        CurrentBrowserWorkspace.ActiveTabId = _activeBrowserTab?.Id;
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        _activeBrowserTab = null;
        _browser.SetActiveView(null);
    }
    private async Task AttachBrowserTabsAsync() {
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        foreach (var tab in _browserTabs) {
            if (!BrowserContentHost.Children.Contains(tab.View))
                BrowserContentHost.Children.Add(tab.View);
            _vm.Tabs.AddBrowserTab(tab.Id, tab.View.TryCore()?.DocumentTitle, false);
            await RefreshBrowserTabIconAsync(tab);
        }
    }
}
