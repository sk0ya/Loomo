namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ワークスペース切替とスナップショット保存・復元（タブ実体の付け替え）</summary>
public partial class ShellWindow {
    private readonly WorkspaceSwitchRequestCoordinator _workspaceSwitchRequests = new();
    private readonly WorkspaceTransitionGate _workspaceTransition = new();

    private void OnSidebarTabActivated(object? sender, TabEntryViewModel tab) {
        switch (tab.Kind) {
            case TabEntryKind.Terminal:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
                ActivateTerminalTab(tab.Id);
                break;
            case TabEntryKind.Editor:
                // ファイルを開いたときと同じ判定（EditorSupport が前に出ていれば Editor を割り込ませない）。
                EnsureEditorPaneForOpenedFile(_editorTabs.FirstOrDefault(t => t.Id == tab.Id)?.PeekFilePath);
                ActivateEditorTab(tab.Id);
                break;
            case TabEntryKind.Browser:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
                ActivateBrowserTab(tab.Id);
                break;
        }
    }
    private async void OnSidebarTabCloseRequested(object? sender, TabEntryViewModel tab) {
        await CloseSidebarTabsAsync(tab, WorkspaceTabCloseScope.Selected);
    }
    private async void OnSidebarTabCloseOthersRequested(object? sender, TabEntryViewModel tab) {
        await CloseSidebarTabsAsync(tab, WorkspaceTabCloseScope.Others);
    }
    private async void OnSidebarTabCloseAllRequested(object? sender, TabEntryViewModel tab) {
        await CloseSidebarTabsAsync(tab, WorkspaceTabCloseScope.All);
    }
    private async Task CloseSidebarTabsAsync(TabEntryViewModel selected, WorkspaceTabCloseScope scope) {
        var plan = WorkspaceTabClosePolicy.CreatePlan(
            selected.Kind, selected.Id, scope,
            _terminalTabs.Select(tab => tab.Id),
            _editorTabs.Select(tab => tab.Id),
            _browserTabs.Select(tab => tab.Id));
        await WorkspaceTabCloseCoordinator.ExecuteAsync(
            plan, CloseTerminalTabAsync, CloseEditorTab, CloseBrowserTabAsync);
    }
    // 見出しはコマンドのたびに変わるが、端末は保存しないのでスナップショットは書かない（表示だけ更新）。
    private void UpdateTerminalTab(TerminalTab tab, string? title)
        => _vm.Tabs.UpdateTerminalTab(tab.Id, title);
    private void UpdateEditorTab(EditorTab tab) {
        if (ReferenceEquals(_previewEditorTab, tab) && tab.Control.IsModified)
            SetPreviewTab(null);
        var title = tab.Control.IsVirtualDocument && !string.IsNullOrEmpty(tab.VirtualTitle)
            ? tab.VirtualTitle
            : tab.Control.FilePath;
        _vm.Tabs.UpdateEditorTab(tab.Id, title, tab.Control.IsModified);
        SaveActiveWorkspaceSnapshot();
    }
    private void OnWorkspaceActivated(object? sender, WorkspaceSnapshot workspace)
    {
        _taskbarWorkspaceRecent.AddRecent(workspace);
        RequestWorkspaceSwitch(workspace, captureCurrent: true);
    }

    /// <summary>
    /// 切替要求を最新の1件に畳み、切替本体を1本だけ走らせる。
    /// ワークスペース切替は途中で別の切替と並行すると、ペインの付け替えと保存が競合するため、
    /// async void のイベントハンドラから直接実行しない。
    /// </summary>
    private void RequestWorkspaceSwitch(WorkspaceSnapshot workspace, bool captureCurrent)
    {
        if (!_workspaceSwitchRequests.Queue(workspace, captureCurrent))
            return;

        _ = _workspaceSwitchRequests.DrainAsync(
            async () => await Dispatcher.Yield(DispatcherPriority.Background),
            request => SwitchWorkspaceAsync(request.Workspace, request.CaptureCurrent),
            ex => ToastService.Error($"ワークスペースの切替に失敗しました: {ex.Message}"));
    }
    private async void OnWorkspaceRemoved(object? sender, Guid workspaceId) {
        await WorkspaceSessionCoordinator.DisposeWorkspaceTabsAsync(
            workspaceId, _terminalWorkspaces, _editorWorkspaces, _browserWorkspaces);
    }
    private async Task SwitchWorkspaceAsync(WorkspaceSnapshot workspace, bool captureCurrent, bool deferHydration = false) {
        // 切替は await をまたぐ。その間に届いたファイルを開く要求を、前のワークスペースのタブ集合へ入れさせない。
        using var transition = _workspaceTransition.Begin();
        using var profile = WorkspaceSwitchProfiler.Begin(workspace.Name);
        if (captureCurrent)
            SaveActiveWorkspaceSnapshot(immediate: true);
        profile?.Lap("save");
        var trailSaved = _trailSuppressed;
        _trailSuppressed = true;
        try {
            await _vm.Trail.SetWorkspaceAsync(workspace.Id.ToString());
            _vm.Trail.EnsureLoaded();
            _trailPaneCommit.Reset();   // ペイン切替のデデュープも新しいワークスペースで仕切り直す
            profile?.Lap("trail");
            await SwitchWorkspaceCoreAsync(workspace, deferHydration, profile);
        } finally {
            _trailSuppressed = trailSaved;
        }
    }
    private async Task SwitchWorkspaceCoreAsync(
        WorkspaceSnapshot workspace, bool deferHydration, WorkspaceSwitchProfiler? profile = null) {
        ClearStageModeForWorkspaceSwitch();
        _vm.SearchPanel.ClearQuery();
        DetachTerminalTabs();
        DetachEditorTabs();
        DetachBrowserTabs();
        _detached?.CloseAll();
        _activeWorkspace = workspace;
        _vm.Recent.SetWorkspace(workspace);
        profile?.Lap("detach");
        if (!deferHydration) {
            _vm.FolderTree.SetPendingViewState(workspace.TreeExpandedPaths, workspace.TreeSelectedPath);
            _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
            _vm.FolderTree.RestoreAdditionalFolders(workspace.AdditionalFolders);
            // ファイル一覧はパンくずの起点・ピン・書き込み可否をワークスペースのフォルダー集合から
            // 決めるので、それが確定する LoadRoot のあとで復元する。
            _vm.Files.Restore(workspace.Files?.Migrate(), workspace.RootPath);
            RestoreGitCompareBase(workspace);
            StartupProfiler.Mark("  復元:FolderTree.LoadRoot");
        }
        profile?.Lap("folderTree");
        RestoreComposer(workspace);
        _vm.Pegboard.LoadItems(workspace.Pegboard);
        LoadLayouts(workspace.Layouts, workspace.ScratchLayout, workspace.ActiveLayoutIndex, workspace.LayoutDirty);
        ApplyIdePaneApplicability(WorkspaceSessionCoordinator.WorkspaceFolders(workspace));
        LoadEnabledSessions(workspace.EnabledSessions);
        _activeWingTab = workspace.ActiveWingTab;
        var restoredMode = WorkspaceSessionCoordinator.ResolveDisplayMode(workspace);
        PrepareStageSnapshot(restoredMode == DisplayMode.Solo, workspace.Stage);
        PrepareDockSnapshot(restoredMode == DisplayMode.Dock, workspace.Dock);
        StartupProfiler.Mark("  復元:PrepareStageSnapshot");
        profile?.Lap("viewModels");
        ApplyPaneLayout(workspace.PaneLayout);
        if (_isSpanMaximized)
            ReapplySpanPaneLayout();
        StartupProfiler.Mark("  復元:ApplyPaneLayout");
        profile?.Lap("paneLayout");
        if (!deferHydration)
            await Dispatcher.Yield(DispatcherPriority.Background);
        if (deferHydration) {
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            StartupProfiler.Mark("  復元:初フレーム後に継続");
            _vm.FolderTree.SetPendingViewState(workspace.TreeExpandedPaths, workspace.TreeSelectedPath);
            _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
            _vm.FolderTree.RestoreAdditionalFolders(workspace.AdditionalFolders);
            _vm.Files.Restore(workspace.Files?.Migrate(), workspace.RootPath);
            RestoreGitCompareBase(workspace);
            StartupProfiler.Mark("  復元:FolderTree.LoadRoot（遅延）");
            profile?.Lap("deferredFolderTree");
        }
        RestoreTerminalTabs(workspace, profile);
        StartupProfiler.Mark("  復元:RestoreTerminalTabs");
        profile?.Lap("terminal");
        await Dispatcher.Yield(DispatcherPriority.Background);
        RestoreEditorTabs(workspace, profile);
        StartupProfiler.Mark("  復元:RestoreEditorTabs");
        profile?.Lap("editor");
        await Dispatcher.Yield(DispatcherPriority.Background);
        await RestoreBrowserTabsAsync(workspace);
        StartupProfiler.Mark("  復元:RestoreBrowserTabs");
        profile?.Lap("browser");
        CompleteStageSnapshotRestore();
        CompleteDockSnapshotRestore();
        RestoreActivePane(workspace);
        if (workspace.DetachedWindows.Count > 0)
            Detached.Restore(workspace.DetachedWindows, RestoreDetachedItem);
        StartupProfiler.Mark("  復元:CompleteStageSnapshotRestore");
        profile?.Lap("stage");
        SaveActiveWorkspaceSnapshot();
        profile?.Lap("scheduleSave");
    }
    // Git の比較基準はワークスペースごとの現在地。**LoadRoot（＝_workspace.OpenFolder）のあと**で戻す
    // ——前に置くと Git の対象フォルダーがまだ切り替わっておらず、VM が走らせるブランチ候補の読込が
    // 「ワークスペースフォルダーが開かれていません」で空を返し、保存した枝が既定ブランチへすり替わる
    // （deferHydration の経路が本番の起動経路なので、そこでこそ踏む）。
    private void RestoreGitCompareBase(WorkspaceSnapshot workspace)
        => _vm.GitPanel.CompareBase.Restore(workspace.GitCompare);

    private void SaveActiveWorkspaceSnapshot(bool immediate = false) {
        if (_activeWorkspace is null)
            return;
        RecordTrailLayoutIfChanged();
        _pendingWorkspaceSnapshotSave = WorkspaceSnapshotSaveScheduler.Schedule(
            Dispatcher, _pendingWorkspaceSnapshotSave, immediate,
            SaveActiveWorkspaceSnapshotNow, () => _pendingWorkspaceSnapshotSave = null);
    }
    /// <summary>いまの状態を書き出す。<paramref name="immediate"/> が false（打鍵・タブ切替ごとの定期保存）なら、
    /// UI スレッドでやるのは状態の組み立てまでで、ディスクへの書き出しは書き出し専用スレッドが引き取る
    /// （実測 9〜10ms／回のほとんどはディスク。§31.15）。切替・終了時だけ同期で書き切る。</summary>
    private void SaveActiveWorkspaceSnapshotNow(bool immediate) {
        if (_activeWorkspace is null)
            return;
        CaptureInto(_activeWorkspace);
        _vm.Workspaces.SaveSnapshot(_activeWorkspace, immediate);
        RefreshLatestTrailPaneLayout();
    }
    private void CaptureInto(WorkspaceSnapshot snapshot) {
        snapshot.LastUsedUtc = DateTime.UtcNow;
        snapshot.Name = WorkspaceListViewModel.DisplayName(snapshot.RootPath);
        WorkspaceSessionCoordinator.CaptureEditorTabs(snapshot, _editorTabs, _activeEditorTab?.Id);
        snapshot.BrowserTabs = WorkspaceSessionCoordinator.CaptureBrowserTabs(
            _browserTabs, _activeBrowserTab?.Id);
        snapshot.DetachedWindows = _detached?.Capture(CaptureDetachedItem) ?? new();
        snapshot.PinnedFolders = _vm.FolderTree.PinnedFolders.ToList();
        snapshot.TreeRootPath = _vm.FolderTree.TreeRootOverride;
        snapshot.TreeExpandedPaths = _vm.FolderTree.CaptureExpandedPaths().ToList();
        snapshot.TreeSelectedPath = _vm.FolderTree.CaptureSelectedPath();
        snapshot.AdditionalFolders = _vm.FolderTree.CaptureAdditionalFolders().ToList();
        snapshot.Files = _vm.Files.Capture();
        snapshot.GitCompare = _vm.GitPanel.CompareBase.Capture();
        snapshot.ComposerText = CaptureComposerText();
        snapshot.ComposerVisible = IsComposerVisible;
        snapshot.ComposerHeight = CaptureComposerHeight();
        snapshot.Pegboard = _vm.Pegboard.ToSnapshots();
        WorkspaceSessionCoordinator.CaptureDisplayState(
            snapshot, CurrentDisplayMode, _enabledSessions, _activeWingTab,
            WorkspaceSessionCoordinator.CaptureStage(
                _stageActive, _stagePane, _overviewActive, _wingWidth, _isWingCollapsed),
            CaptureDockSnapshot(), _stageActive, _stagePane, _focusedRegion?.Pane);
        WorkspaceSessionCoordinator.CaptureLayouts(
            snapshot, _layouts, _scratchLayout, _activeLayoutIndex, _layoutDirty,
            _editorViews?.Capture());
        WorkspaceSessionCoordinator.CapturePaneLayout(
            snapshot, _isSpanMaximized, _spanSavedRoot, _root,
            CaptureLayoutSizes, ToSnapshot);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
        => SaveActiveWorkspaceSnapshot(immediate: true);
    private void OnClosed(object? sender, EventArgs e) {
        CancelFileDropOperations();
        CancelFileAiPreparation();
        DisposeCSharpDiagnosticsWiring();
        _lspWorkspace.DiagnosticsPublished -= OnLspDiagnosticsPublished;
        // 外さないと、閉じたあとにサーバーが applyEdit を投げてきたとき死んだ Dispatcher を叩く。
        _lspWorkspace.ApplyEditRequested -= OnLspServerApplyEditRequested;
        _workspace.FoldersChanged -= OnProblemWorkspaceFoldersChanged;
        _detached?.CloseAll();
        _editorSupportFileWatcher?.Dispose();   // 閉じたあとに死んだ Dispatcher を叩かせない（§24.8）
        _editorSupport.WebView.Dispose();
        _editorSupport.Visuals.Dispose();
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
