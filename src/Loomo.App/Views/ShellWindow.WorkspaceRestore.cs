

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ワークスペース復元時のタブ実体の付け替え（端末／エディタ／ブラウザの
/// Restore・Attach・Detach・GetOrCreate）。切替の入口とスナップショット保存は ShellWindow.Workspaces.cs。</summary>
public partial class ShellWindow
{
    private void RestoreTerminalTabs(WorkspaceSnapshot workspace)
    {
        var terminalWorkspace = GetOrCreateTerminalWorkspace(workspace.Id);
        _activeTerminalWorkspace = terminalWorkspace;
        _terminalTabs = terminalWorkspace.Tabs;

        if (terminalWorkspace.IsInitialized && _terminalTabs.Count > 0)
        {
            AttachTerminalTabs();
            ActivateTerminalTab(terminalWorkspace.ActiveTabId ?? _terminalTabs[0].Id);
            return;
        }

        terminalWorkspace.IsInitialized = true;
        var snapshots = workspace.TerminalTabs.Count == 0
            ? new[]
            {
                new TerminalTabSnapshot
                {
                    WorkingDirectory = workspace.Terminal.WorkingDirectory,
                    Title = workspace.Terminal.Title ?? "Terminal",
                    IsActive = true
                }
            }
            : workspace.TerminalTabs.ToArray();

        foreach (var snapshot in snapshots)
        {
            var cwd = Directory.Exists(snapshot.WorkingDirectory) ? snapshot.WorkingDirectory! : workspace.RootPath;
            var tab = CreateTerminalTab(cwd, snapshot.Id == Guid.Empty ? null : snapshot.Id);
            _terminalTabs.Add(tab);
            _vm.Tabs.AddTerminalTab(tab.Id, snapshot.Title ?? tab.View.HeaderTitle, false);
        }

        var active = snapshots.FirstOrDefault(t => t.IsActive) ?? snapshots.First();
        ActivateTerminalTab(active.Id == Guid.Empty ? _terminalTabs[0].Id : active.Id);
    }

    private void RestoreEditorTabs(WorkspaceSnapshot workspace)
    {
        var editorWorkspace = GetOrCreateEditorWorkspace(workspace.Id);
        _activeEditorWorkspace = editorWorkspace;
        _editorTabs = editorWorkspace.Tabs;

        if (editorWorkspace.IsInitialized && _editorTabs.Count > 0)
        {
            AttachEditorTabs();
            ActivateEditorTab(editorWorkspace.ActiveTabId ?? _editorTabs[0].Id);
            return;
        }

        editorWorkspace.IsInitialized = true;
        var snapshots = workspace.EditorTabs.Count == 0
            ? new[]
            {
                new EditorTabSnapshot
                {
                    FilePath = workspace.Editor.FilePath,
                    Text = workspace.Editor.Text,
                    IsModified = workspace.Editor.IsModified,
                    IsActive = true
                }
            }
            : workspace.EditorTabs.ToArray();

        foreach (var snapshot in snapshots)
        {
            var tab = CreatePendingEditorTab(snapshot);
            _editorTabs.Add(tab);
            _vm.Tabs.AddEditorTab(tab.Id, snapshot.FilePath, snapshot.IsModified, false);
        }

        var active = snapshots.FirstOrDefault(t => t.IsActive) ?? snapshots.First();
        ActivateEditorTab(active.Id == Guid.Empty ? _editorTabs[0].Id : active.Id);
    }

    private TerminalWorkspaceTabs GetOrCreateTerminalWorkspace(Guid workspaceId)
    {
        if (_terminalWorkspaces.TryGetValue(workspaceId, out var terminalWorkspace))
            return terminalWorkspace;

        terminalWorkspace = new TerminalWorkspaceTabs();
        _terminalWorkspaces[workspaceId] = terminalWorkspace;
        return terminalWorkspace;
    }

    private EditorWorkspaceTabs GetOrCreateEditorWorkspace(Guid workspaceId)
    {
        if (_editorWorkspaces.TryGetValue(workspaceId, out var editorWorkspace))
            return editorWorkspace;

        editorWorkspace = new EditorWorkspaceTabs();
        _editorWorkspaces[workspaceId] = editorWorkspace;
        return editorWorkspace;
    }

    private void DetachTerminalTabs()
    {
        CurrentTerminalWorkspace.ActiveTabId = _activeTerminalTab?.Id;
        _terminalViews?.Reset();
        _vm.Tabs.TerminalTabs.Clear();
        _activeTerminalTab = null;
    }

    private void AttachTerminalTabs()
    {
        _terminalViews?.Reset();
        _vm.Tabs.TerminalTabs.Clear();

        foreach (var tab in _terminalTabs)
            _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false);
    }

    private void DetachEditorTabs()
    {
        _editorSupportDebounceTimer?.Stop();
        DetachEditorSupportSource();
        _editorSupport.WebView.ResetPageState();
        _editorSupport.IsPinned = false;
        UpdateEditorSupportPinToggle();
        CurrentEditorWorkspace.ActiveTabId = _activeEditorTab?.Id;
        _editorViews?.Reset();
        _vm.Tabs.EditorTabs.Clear();
        _activeEditorTab = null;
        _previewEditorTab = null;
    }

    private void AttachEditorTabs()
    {
        _editorViews?.Reset();
        _vm.Tabs.EditorTabs.Clear();

        foreach (var tab in _editorTabs)
            _vm.Tabs.AddEditorTab(tab.Id, tab.PeekFilePath, tab.PeekIsModified, false);
    }

    private async Task RestoreBrowserTabsAsync(WorkspaceSnapshot workspace)
    {
        var browserWorkspace = GetOrCreateBrowserWorkspace(workspace.Id);
        _activeBrowserWorkspace = browserWorkspace;
        _browserTabs = browserWorkspace.Tabs;

        if (browserWorkspace.IsInitialized && _browserTabs.Count > 0)
        {
            await AttachBrowserTabsAsync();
            ActivateBrowserTab(browserWorkspace.ActiveTabId ?? _browserTabs[0].Id);
            return;
        }

        browserWorkspace.IsInitialized = true;
        var snapshots = workspace.BrowserTabs;
        var tabs = snapshots.Count == 0
            ? new[] { new BrowserTabSnapshot { Url = DefaultBrowserUrl, Title = "Browser", IsActive = true } }
            : snapshots.ToArray();

        foreach (var snapshot in tabs)
            CreateBrowserTab(
                snapshot.Url ?? DefaultBrowserUrl,
                snapshot.Id == Guid.Empty ? null : snapshot.Id,
                snapshot.Title);

        var active = tabs.FirstOrDefault(t => t.IsActive) ?? tabs.First();
        ActivateBrowserTab(active.Id);
    }

    private BrowserWorkspaceTabs GetOrCreateBrowserWorkspace(Guid workspaceId)
    {
        if (_browserWorkspaces.TryGetValue(workspaceId, out var browserWorkspace))
            return browserWorkspace;

        browserWorkspace = new BrowserWorkspaceTabs();
        _browserWorkspaces[workspaceId] = browserWorkspace;
        return browserWorkspace;
    }

    private void DetachBrowserTabs()
    {
        CurrentBrowserWorkspace.ActiveTabId = _activeBrowserTab?.Id;
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        _activeBrowserTab = null;
        _browser.SetActiveView(null);
    }

    private async Task AttachBrowserTabsAsync()
    {
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();

        foreach (var tab in _browserTabs)
        {
            if (!BrowserContentHost.Children.Contains(tab.View))
                BrowserContentHost.Children.Add(tab.View);

            _vm.Tabs.AddBrowserTab(tab.Id, tab.View.CoreWebView2?.DocumentTitle, false);
            await RefreshBrowserTabIconAsync(tab);
        }
    }
}

