using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナル／エディタのタブ管理（作成・選択・クローズ・プレビュータブ）</summary>
public partial class ShellWindow {
    private EditorTabActivationPresenter? _editorTabActivationPresenter;
    private EditorTabActivationPresenter EditorTabActivationUi
        => _editorTabActivationPresenter ??= new(
            Dispatcher, EditorTabStripScrollViewer, EditorTabStripItems, () => _activeEditorTab);

    private void OnTerminalNewTab(object sender, RoutedEventArgs e) {
        var startDir = WorkspaceSessionCoordinator.ResolveWorkingDirectory(
            _activeTerminalTab?.View.WorkingDirectory,
            _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory) ?? _terminal.CurrentDirectory;
        var tab = CreateTerminalTab(startDir);
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);
        ActivateTerminalTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }
    private void OnTerminalTabSelected(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateTerminalTab(id);
    }
    private async void OnTabMiddleClick(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { Tag: Guid id })
            return;
        e.Handled = true;
        var kind = WorkspaceTabClosePolicy.ResolveKind(
            id, _terminalTabs.Select(tab => tab.Id),
            _editorTabs.Select(tab => tab.Id), _browserTabs.Select(tab => tab.Id));
        if (!await WorkspaceTabCloseCoordinator.ExecuteOneAsync(
                kind, id, CloseTerminalTabAsync, CloseEditorTab, CloseBrowserTabAsync))
            return;
        SaveActiveWorkspaceSnapshot();
    }
    private async void OnTerminalTabClosed(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id }) {
            await CloseTerminalTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }
    private void OnEditorTabSelected(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateEditorTab(id);
    }
    private TerminalWorkspaceTabs CurrentTerminalWorkspace
        => _activeTerminalWorkspace ?? _scratchTerminalWorkspace;
    private EditorWorkspaceTabs CurrentEditorWorkspace
        => _activeEditorWorkspace ?? _scratchEditorWorkspace;
    private void ActivateTerminalTab(
        Guid id, WorkspaceSwitchProfiler? profile = null, bool focusView = true) {
        var tab = _terminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;
        _terminalViews?.Activate(id, focusView);
        profile?.Lap("terminal.views");
        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = id;
        _terminal.Attach(tab.View);
        profile?.Lap("terminal.serviceAttach");
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        profile?.Lap("terminal.cwd");
        _vm.Tabs.ActivateTerminalTab(id);
        profile?.Lap("terminal.tabVm");
        RecordTrailTerminalTab(tab);
        SaveActiveWorkspaceSnapshot();
        profile?.Lap("terminal.bookkeeping");
    }
    private void SetActiveTerminalTab(TerminalTab tab) {
        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = tab.Id;
        _terminal.Attach(tab.View);
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        _vm.Tabs.ActivateTerminalTab(tab.Id);
        RecordTrailTerminalTab(tab);
    }
    private async Task CloseTerminalTabAsync(Guid id) {
        var index = _terminalTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;
        var wasActive = _activeTerminalTab?.Id == id;
        var tab = _terminalTabs[index];
        ViewportTree.Detach(tab.View);
        await tab.View.CloseAsync();
        _terminalTabs.RemoveAt(index);
        _vm.Tabs.RemoveTerminalTab(id);
        _terminalViews?.RemoveTab(id);
        ForgetTerminalActivity(id);
        PaneTabTransferCoordinator.CompleteRemoval(
            _terminalTabs, t => t.Id, _terminalViews, index, wasActive,
            id => ActivateTerminalTab(id),
            focusedId => {
                if (_terminalTabs.FirstOrDefault(t => t.Id == focusedId) is { } focused)
                    SetActiveTerminalTab(focused);
            },
            () => {
                var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
                var newTab = CreateTerminalTab(startDir);
                _terminalTabs.Add(newTab);
                _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
                ActivateTerminalTab(newTab.Id);
            });
    }
    private void ActivateEditorTab(
        Guid id, WorkspaceSwitchProfiler? profile = null, bool focusView = true) {
        var tab = _editorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;
        _editorViews?.Activate(id, focusView);
        profile?.Lap("editor.views");
        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = id;
        _editor.Attach(tab.Control);
        profile?.Lap("editor.serviceAttach");
        _vm.Tabs.ActivateEditorTab(id);
        profile?.Lap("editor.tabVm");
        EditorTabActivationUi.OnActivated(tab);
        SwitchEditorSupportSource(tab);
        profile?.Lap("editor.support");
        RecordTrailEditorTab(tab);
        OnActiveEditorFileChanged(tab);
        SaveActiveWorkspaceSnapshot();
        profile?.Lap("editor.bookkeeping");
    }
    private void SetActiveEditorTab(EditorTab tab) {
        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = tab.Id;
        _editor.Attach(tab.Control);
        _vm.Tabs.ActivateEditorTab(tab.Id);
        EditorTabActivationUi.OnActivated(tab);
        SwitchEditorSupportSource(tab);
        RecordTrailEditorTab(tab);
        OnActiveEditorFileChanged(tab);
    }
    // アクティブなエディタが指すファイルが変わったときの唯一の評価点。
    // 以前は ActivateEditorTab / SetActiveEditorTab の二本立てで後者にしか評価が無く、しかも
    // 新規タブは Activate→LoadFile の順なので評価時点でパスが未設定だった（設計書 §30.6 P3）。
    // LoadFile の後にもう一度呼べるよう、同じパスの二度目は捨てる。
    private void OnActiveEditorFileChanged(EditorTab tab) {
        var filePath = tab.IsRealized ? tab.Control.FilePath : tab.PeekFilePath;
        _vm.Debug.Problems.CurrentFilePath = filePath;
        _vm.TsIde.Problems.CurrentFilePath = filePath;
        // 読み込みを伴わないタブ活性化ぶんの再送（読み込みを伴う経路は LoadEditorFile が受け持つ）。
        // 同じ内容なら Editor 側が no-op にするので、重複して送っても害はない。
        if (tab.IsRealized) {
            SyncEditorTestGlyphs(tab.Control);
            SyncEditorCodeActionBulb(tab.Control);
        }
        _editorLspNotices.ShowForFile(filePath);
    }
    private void CloseEditorTab(Guid id) {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;
        var wasActive = _activeEditorTab?.Id == id;
        var tab = _editorTabs[index];
        if (ReferenceEquals(_editorSupport.Source, tab)) {
            _editorSupportDebounceTimer?.Stop();
            DetachEditorSupportSource();
            _editorSupport.IsPinned = false;
            UpdateEditorSupportPinToggle();
        }
        if (tab.IsRealized) {
            // 破棄する前に C# 診断（StyleCop／compiler フォールバック）の購読と保持を外す。
            // 外さないと解析中のタスクが Dispose 済みコントロールへ結果を書き戻し、
            // 閉じたファイルの問題も「問題」一覧に残り続ける。
            ClearStyleCopPresentation(tab.Control);
            ViewportTree.Detach(tab.Control);
            tab.Control.Dispose();
        }
        if (ReferenceEquals(_previewEditorTab, tab))
            _previewEditorTab = null;
        if (tab.PeekFilePath is { Length: > 0 } closedPath) {
            _editorSupport.History.Remove(closedPath);
            UpdateEditorSupportNavAffordances();
        }
        _editorTabs.RemoveAt(index);
        _vm.Tabs.RemoveEditorTab(id);
        _editorViews?.RemoveTab(id);
        PaneTabTransferCoordinator.CompleteRemoval(
            _editorTabs, t => t.Id, _editorViews, index, wasActive,
            id => ActivateEditorTab(id),
            focusedId => {
                if (_editorTabs.FirstOrDefault(t => t.Id == focusedId) is { } focused)
                    SetActiveEditorTab(focused);
            },
            () => {
                var newTab = CreateEditorTab();
                _editorTabs.Add(newTab);
                _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
                ActivateEditorTab(newTab.Id);
            });
    }
    private void OnFolderTreeEntryRenamed(EntryRenamedEventArgs e) {
        if (e.IsDirectory)
            _vm.Recent.RecordFolder(e.NewPath);
        else
            _vm.Recent.RecordFile(e.NewPath);
        foreach (var tab in _editorTabs) {
            var path = tab.PeekFilePath;
            var newPath = EditorTabNavigationPolicy.PathAfterRename(
                path, e.OldPath, e.NewPath, e.IsDirectory);
            if (newPath is not null)
                RebaseEditorTabPath(tab, newPath);
        }
    }
    /// <summary>
    /// 開いたままのタブを新しいパスへ付け替える。<b>バッファの中身（未保存の編集も）はそのまま</b>で、
    /// 名乗るパスだけが変わる。
    /// <para>
    /// バッファの <c>FilePath</c> を直接書くだけでは足りない。拡張子から決まっているもの——
    /// シンタックスハイライトの言語、LSP のドキュメント URI（＝担当サーバーごと変わる）、
    /// 外部変更を見るウォッチャ、相棒ペイン（EditorSupport）の種類——が旧拡張子のまま固まる。
    /// 言語判定はコントロール内部なのでホストからは触れず、<c>RebaseFilePath</c>（Editor 1.0.78）に任せる。
    /// </para>
    /// </summary>
    private void RebaseEditorTabPath(EditorTab tab, string newPath) {
        if (tab.IsRealized) {
            tab.Control.RebaseFilePath(newPath);
            UpdateEditorTab(tab);   // タブ名更新＋スナップショット保存
            // 相棒ペインの種類（md プレビュー／CSV 表／コードのアウトライン）は毎回の描画で
            // パスから決め直すので、追従元が自分なら描き直しを1回頼めばよい。
            if (ReferenceEquals(_editorSupport.Source, tab))
                InvalidateEditorSupport();
            if (ReferenceEquals(_activeEditorTab, tab))
                OnActiveEditorFileChanged(tab);   // LSP の促し・問題一覧の対象ファイル
        } else if (tab.Pending is { } pending) {
            pending.FilePath = newPath;
            pending.Title = Path.GetFileName(newPath);
            _vm.Tabs.UpdateEditorTab(tab.Id, newPath, pending.IsModified);
            SaveActiveWorkspaceSnapshot();
        }
    }
    private void OnFolderTreeEntryDeleted(string deletedPath) {
        var affected = EditorTabNavigationPolicy.TabsAffectedByDeletion(_editorTabs, deletedPath);
        if (affected.Count == 0)
            return;
        foreach (var id in affected)
            CloseEditorTab(id);
        SaveActiveWorkspaceSnapshot();
    }
    private void RevealActiveFileInFolderTree() {
        var path = _activeEditorTab?.PeekFilePath;
        if (string.IsNullOrEmpty(path))
            return;
        _vm.RevealExplorerPanel();
        // ツリーは SidebarContainer の直下ではなく、段ごとの区画ホスト（上段／中段）の子として
        // 住む——しかもドラッグで段を移せる。可視の子を探す作りでは同期が黙って効かなくなるので名前で持つ。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => SidebarFolderTree.RevealPath(path)));
    }
}
