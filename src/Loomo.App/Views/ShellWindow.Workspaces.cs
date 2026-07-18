
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ワークスペース切替とスナップショット保存・復元（タブ実体の付け替え）</summary>
public partial class ShellWindow {

    private void OnSidebarTabActivated(object? sender, TabEntryViewModel tab) {
        switch (tab.Kind) {
            case TabEntryKind.Terminal:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
                ActivateTerminalTab(tab.Id);
                break;
            case TabEntryKind.Editor:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
                ActivateEditorTab(tab.Id);
                break;
            case TabEntryKind.Browser:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
                ActivateBrowserTab(tab.Id);
                break;
        }
    }

    private async void OnSidebarTabCloseRequested(object? sender, TabEntryViewModel tab) {
        switch (tab.Kind) {
            case TabEntryKind.Terminal:
                await CloseTerminalTabAsync(tab.Id);
                break;
            case TabEntryKind.Editor:
                CloseEditorTab(tab.Id);
                break;
            case TabEntryKind.Browser:
                await CloseBrowserTabAsync(tab.Id);
                break;
        }
    }

    private async void OnSidebarTabCloseOthersRequested(object? sender, TabEntryViewModel tab) {
        switch (tab.Kind) {
            case TabEntryKind.Terminal:
                foreach (var id in _terminalTabs.Where(t => t.Id != tab.Id).Select(t => t.Id).ToList())
                    await CloseTerminalTabAsync(id);
                break;
            case TabEntryKind.Editor:
                foreach (var id in _editorTabs.Where(t => t.Id != tab.Id).Select(t => t.Id).ToList())
                    CloseEditorTab(id);
                break;
            case TabEntryKind.Browser:
                foreach (var id in _browserTabs.Where(t => t.Id != tab.Id).Select(t => t.Id).ToList())
                    await CloseBrowserTabAsync(id);
                break;
        }
    }

    private async void OnSidebarTabCloseAllRequested(object? sender, TabEntryViewModel tab) {
        switch (tab.Kind) {
            case TabEntryKind.Terminal:
                foreach (var id in _terminalTabs.Select(t => t.Id).ToList())
                    await CloseTerminalTabAsync(id);
                break;
            case TabEntryKind.Editor:
                foreach (var id in _editorTabs.Select(t => t.Id).ToList())
                    CloseEditorTab(id);
                break;
            case TabEntryKind.Browser:
                foreach (var id in _browserTabs.Select(t => t.Id).ToList())
                    await CloseBrowserTabAsync(id);
                break;
        }
    }

    private void UpdateTerminalTab(TerminalTab tab, string? title) {
        _vm.Tabs.UpdateTerminalTab(tab.Id, title);
        SaveActiveWorkspaceSnapshot();
    }

    private void UpdateEditorTab(EditorTab tab) {
        if (ReferenceEquals(_previewEditorTab, tab) && tab.Control.IsModified)
            SetPreviewTab(null);

        var title = tab.Control.IsVirtualDocument && !string.IsNullOrEmpty(tab.VirtualTitle)
            ? tab.VirtualTitle
            : tab.Control.FilePath;
        _vm.Tabs.UpdateEditorTab(tab.Id, title, tab.Control.IsModified);
        SaveActiveWorkspaceSnapshot();
    }

    private async void OnWorkspaceActivated(object? sender, WorkspaceSnapshot workspace)
        => await SwitchWorkspaceAsync(workspace, captureCurrent: true);

    private async void OnWorkspaceRemoved(object? sender, Guid workspaceId) {
        if (_terminalWorkspaces.Remove(workspaceId, out var terminal)) {
            foreach (var tab in terminal.Tabs)
                await tab.View.CloseAsync();
        }

        if (_browserWorkspaces.Remove(workspaceId, out var browser)) {
            foreach (var tab in browser.Tabs)
                tab.View.Dispose();
        }

        if (_editorWorkspaces.Remove(workspaceId, out var editor))
            foreach (var tab in editor.Tabs)
                if (tab.IsRealized)
                    tab.Control.Dispose();
    }

    private void OnCopyWorkspacePathMenuClick(object sender, RoutedEventArgs e) {
        if (sender is not MenuItem { DataContext: WorkspaceEntryViewModel entry })
            return;

        try { Clipboard.SetText(entry.RootPath); }
        catch { /* クリップボードのロック等は無視 */ }
    }

    private void OnRevealWorkspaceInExplorerMenuClick(object sender, RoutedEventArgs e) {
        if (sender is not MenuItem { DataContext: WorkspaceEntryViewModel entry })
            return;

        try {
            if (Directory.Exists(entry.RootPath))
                Process.Start("explorer.exe", $"\"{entry.RootPath}\"");
        } catch {
        }
    }

    private void OnDeleteWorkspaceMenuClick(object sender, RoutedEventArgs e) {
        if (sender is not MenuItem { DataContext: WorkspaceEntryViewModel entry })
            return;

        if (!_vm.Workspaces.RemoveWorkspaceCommand.CanExecute(entry)) {
            MessageBox.Show( this, "最後のワークスペースは削除できません（常に1つは開いている必要があります）。", "ワークスペースの削除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show( this, $"ワークスペース「{entry.Name}」を一覧から削除しますか？\n" +
            "フォルダ自体は削除されません（タブ・レイアウトの保存状態は失われます）。", "ワークスペースの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (result == MessageBoxResult.OK)
            _vm.Workspaces.RemoveWorkspaceCommand.Execute(entry);
    }

    private async Task SwitchWorkspaceAsync(WorkspaceSnapshot workspace, bool captureCurrent, bool deferHydration = false) {
        if (captureCurrent)
            SaveActiveWorkspaceSnapshot(immediate: true);

        var trailSaved = _trailSuppressed;
        _trailSuppressed = true;
        try {
            _vm.Trail.SetWorkspace(workspace.Id.ToString());
            _vm.Trail.EnsureLoaded();
            _trailLastPane = null;   // ペイン切替のデデュープも新しいワークスペースで仕切り直す
            _trailLastPaneMode = null;
            await SwitchWorkspaceCoreAsync(workspace, deferHydration);
        } finally {
            _trailSuppressed = trailSaved;
        }
    }

    private async Task SwitchWorkspaceCoreAsync(WorkspaceSnapshot workspace, bool deferHydration) {
        ClearStageModeForWorkspaceSwitch();
        _vm.SearchPanel.ClearQuery();
        DetachTerminalTabs();
        DetachEditorTabs();
        DetachBrowserTabs();
        _detached?.CloseAll();
        _activeWorkspace = workspace;
        if (!deferHydration) {
            _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
            StartupProfiler.Mark("  復元:FolderTree.LoadRoot");
        }
        RestoreComposer(workspace);
        _vm.Pegboard.LoadItems(workspace.Pegboard);
        LoadLayouts(workspace.Layouts, workspace.ScratchLayout, workspace.ActiveLayoutIndex, workspace.LayoutDirty);
        ApplyIdePaneApplicability(workspace.RootPath);
        LoadEnabledSessions(workspace.EnabledSessions);
        PrepareStageSnapshot(WorkspaceSessionCoordinator.ResolveSoloMode(workspace), workspace.Stage);
        StartupProfiler.Mark("  復元:PrepareStageSnapshot");
        ApplyPaneLayout(workspace.PaneLayout);
        if (_isSpanMaximized)
            ReapplySpanPaneLayout();
        StartupProfiler.Mark("  復元:ApplyPaneLayout");

        if (deferHydration) {
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            StartupProfiler.Mark("  復元:初フレーム後に継続");
            _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
            StartupProfiler.Mark("  復元:FolderTree.LoadRoot（遅延）");
        }

        RestoreTerminalTabs(workspace);
        StartupProfiler.Mark("  復元:RestoreTerminalTabs");
        if (deferHydration)
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        RestoreEditorTabs(workspace);
        StartupProfiler.Mark("  復元:RestoreEditorTabs");
        if (deferHydration)
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        await RestoreBrowserTabsAsync(workspace);
        StartupProfiler.Mark("  復元:RestoreBrowserTabs");
        CompleteStageSnapshotRestore();
        if (workspace.DetachedWindows.Count > 0)
            Detached.Restore(workspace.DetachedWindows, RestoreDetachedItem);
        StartupProfiler.Mark("  復元:CompleteStageSnapshotRestore");

        SaveActiveWorkspaceSnapshot();
    }

    private void SaveActiveWorkspaceSnapshot(bool immediate = false) {
        if (_activeWorkspace is null)
            return;

        RecordTrailLayoutIfChanged();

        if (immediate) {
            _pendingWorkspaceSnapshotSave?.Abort();
            _pendingWorkspaceSnapshotSave = null;
            SaveActiveWorkspaceSnapshotNow();
            return;
        }

        if (_pendingWorkspaceSnapshotSave is { Status: DispatcherOperationStatus.Pending })
            return;

        _pendingWorkspaceSnapshotSave = Dispatcher.BeginInvoke( new Action(() => {
                _pendingWorkspaceSnapshotSave = null;
                SaveActiveWorkspaceSnapshotNow();
            }), DispatcherPriority.ApplicationIdle);
    }

    private void SaveActiveWorkspaceSnapshotNow() {
        if (_activeWorkspace is null)
            return;

        CaptureInto(_activeWorkspace);
        _vm.Workspaces.SaveSnapshot(_activeWorkspace);
        RefreshLatestTrailPaneLayout();
    }

    private void CaptureInto(WorkspaceSnapshot snapshot) {
        snapshot.LastUsedUtc = DateTime.UtcNow;
        snapshot.Name = WorkspaceListViewModel.DisplayName(snapshot.RootPath);

        snapshot.TerminalTabs = _terminalTabs.Select(tab => new TerminalTabSnapshot {
            Id = tab.Id, WorkingDirectory = Directory.Exists(tab.View.WorkingDirectory)
                ? tab.View.WorkingDirectory
                : _terminal.CurrentDirectory, Title = tab.View.HeaderTitle, IsActive = tab.Id == _activeTerminalTab?.Id
        }).ToList();

        var activeTerminal = _activeTerminalTab?.View ?? _terminalTabs.FirstOrDefault()?.View;
        if (activeTerminal is not null) {
            snapshot.Terminal.WorkingDirectory = Directory.Exists(activeTerminal.WorkingDirectory)
                ? activeTerminal.WorkingDirectory
                : _terminal.CurrentDirectory;
            snapshot.Terminal.Title = activeTerminal.HeaderTitle;
        }

        var persistableEditorTabs = _editorTabs.Where(tab => !tab.PeekIsVirtual).ToList();
        snapshot.EditorTabs = persistableEditorTabs
            .Select(tab => WorkspaceSessionCoordinator.CaptureEditorTab(tab, _activeEditorTab?.Id))
            .ToList();

        var activeTab = persistableEditorTabs.FirstOrDefault(t => t.Id == _activeEditorTab?.Id)
            ?? persistableEditorTabs.FirstOrDefault();
        if (activeTab is not null) {
            var s = WorkspaceSessionCoordinator.CaptureEditorTab(activeTab, _activeEditorTab?.Id);
            snapshot.Editor.FilePath = s.FilePath;
            snapshot.Editor.Text = s.Text;
            snapshot.Editor.IsModified = s.IsModified;
        }

        snapshot.BrowserTabs = _browserTabs
            .Where(tab => !EditorSupportNavigationService.IsPreviewUrl(tab.View.Source?.ToString()))
            .Select(tab => new BrowserTabSnapshot {
                Id = tab.Id, Url = tab.View.Source?.ToString(), Title = tab.View.CoreWebView2?.DocumentTitle, IsActive = tab.Id == _activeBrowserTab?.Id
            }).ToList();

        snapshot.DetachedWindows = _detached?.Capture(CaptureDetachedItem) ?? new();

        snapshot.PinnedFolders = _vm.FolderTree.PinnedFolders.ToList();
        snapshot.TreeRootPath = _vm.FolderTree.TreeRootOverride;
        snapshot.ComposerText = CaptureComposerText();
        snapshot.ComposerVisible = IsComposerVisible;
        snapshot.ComposerHeight = CaptureComposerHeight();
        snapshot.Pegboard = _vm.Pegboard.ToSnapshots();
        snapshot.Mode = _stageActive ? DisplayMode.Solo : DisplayMode.Layout;
        snapshot.EnabledSessions = _enabledSessions.ToList();
        snapshot.Stage = new StageSnapshot {
            IsActive = _stageActive, Pane = _stageActive ? _stagePane : null, Overview = _stageActive && _overviewActive
        };
        snapshot.Layouts = _layouts.Select(l => new SavedLayout { Name = l.Name, Tree = l.Tree }).ToList();
        snapshot.ScratchLayout = _scratchLayout;
        snapshot.ActiveLayoutIndex = _activeLayoutIndex;
        snapshot.LayoutDirty = _layoutDirty;

        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot) {
            snapshot.PaneLayout = ToSnapshot(savedRoot);
        } else {
            CaptureLayoutSizes();
            snapshot.PaneLayout = _root is null ? null : ToSnapshot(_root);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
        => SaveActiveWorkspaceSnapshot(immediate: true);

    private void OnClosed(object? sender, EventArgs e) {
        _detached?.CloseAll();

        foreach (var workspace in _editorWorkspaces.Values)
            foreach (var tab in workspace.Tabs)
                if (tab.IsRealized)
                    tab.Control.Dispose();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) {
        if (WindowState == WindowState.Maximized && _isSpanMaximized)
            ExitSpanState();
        UpdateMaximizeGlyph();
    }
}
