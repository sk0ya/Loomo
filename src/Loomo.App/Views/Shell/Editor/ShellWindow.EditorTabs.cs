namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタタブを開く・プレビュータブの使い回し（新規タブ・仮想ドキュメント・ ファイル/プレビューで開く・外部変更の読み直し・プレビュー↔通常の昇格）。選択/クローズ/活性化は ShellWindow.Tabs.cs。</summary>
public partial class ShellWindow {
    private void OnEditorNewTab(object sender, RoutedEventArgs e) {
        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }
    private void OpenVirtualDocumentTab(string title) {
        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.VirtualTitle, title, StringComparison.Ordinal));
        if (existing is not null) {
            ActivateEditorTab(existing.Id);
            return;
        }
        var tab = CreateEditorTab();
        tab.VirtualTitle = title;
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, title, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }
    private async Task OpenFileInNewEditorTabAsync(string path) {
        if (EditorTabNavigationPolicy.NormalizeExistingFilePath(path) is not { } normalizedPath)
            return;
        path = normalizedPath;
        _vm.Recent.RecordFile(path);
        EnsureEditorPaneForOpenedFile(path);
        var existing = EditorTabNavigationPolicy.FindOpenFileTab(_editorTabs, path);
        if (existing is not null) {
            if (ReferenceEquals(_previewEditorTab, existing))
                SetPreviewTab(null);
            ActivateEditorTab(existing.Id);
            await ReloadExistingTabIfChangedAsync(existing);
            return;
        }
        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, path, false, false);
        ActivateEditorTab(tab.Id);
        await LoadEditorFileAsync(tab, path);
        OnActiveEditorFileChanged(tab);   // Activate 時点ではまだパス未設定なので、読み込み後に評価する
        UpdateEditorTab(tab);
        RecordTrailEditorTab(tab);
        InvalidateEditorSupport();
        SaveActiveWorkspaceSnapshot();
    }
    private async Task OpenFileInPreviewTabAsync(string path) {
        if (EditorTabNavigationPolicy.NormalizeExistingFilePath(path) is not { } normalizedPath)
            return;
        path = normalizedPath;
        _vm.Recent.RecordFile(path);
        EnsureEditorPaneForOpenedFile(path);
        var existing = EditorTabNavigationPolicy.FindOpenFileTab(_editorTabs, path);
        if (existing is not null) {
            ActivateEditorTab(existing.Id);
            await ReloadExistingTabIfChangedAsync(existing);
            return;
        }
        var target = EditorTabNavigationPolicy.ResolvePreviewReuseTarget(
            _editorTabs, _previewEditorTab, _activeEditorTab);
        if (target is null) {
            target = CreateEditorTab();
            _editorTabs.Add(target);
            _vm.Tabs.AddEditorTab(target.Id, path, false, false);
        }
        var trailSaved = _trailSuppressed;
        _trailSuppressed = true;
        try { ActivateEditorTab(target.Id); }
        finally { _trailSuppressed = trailSaved; }
        await LoadEditorFileAsync(target, path);
        OnActiveEditorFileChanged(target);   // Activate 時点ではまだパス未設定なので、読み込み後に評価する
        SetPreviewTab(target);
        UpdateEditorTab(target);
        RecordTrailEditorTab(target);
        InvalidateEditorSupport();
        SaveActiveWorkspaceSnapshot();
    }
    private async Task ReloadExistingTabIfChangedAsync(EditorTab tab) {
        if (await EditorTabNavigationPolicy.FindChangedExternalFileAsync(tab) is { } path) {
            await LoadEditorFileAsync(tab, path);
            UpdateEditorTab(tab);
        }
        if (ReferenceEquals(_editorSupport.Source, tab))
            InvalidateEditorSupport();
    }
    /// <summary>検索パネルの置換で書き換わったファイルが開いているタブを読み直す。アクティブタブが
    /// 含まれていれば検索ハイライトも新しい内容で引き直す（置換済みの箇所は一致しなくなるので下線が
    /// 消える＝古い表示のまま「まだ一致している」ように見えるのを防ぐ）。</summary>
    private async Task ReloadEditorTabsAfterReplaceAsync(IReadOnlyList<string> paths, string highlightTerm) {
        var activeAffected = false;
        foreach (var path in paths) {
            var tab = _editorTabs.FirstOrDefault(t =>
                string.Equals(t.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
            if (tab is null || !tab.IsRealized)
                continue;
            await ReloadExistingTabIfChangedAsync(tab);
            if (ReferenceEquals(tab, _activeEditorTab))
                activeAffected = true;
        }
        if (activeAffected)
            _activeEditorTab?.Control.HighlightSearch(highlightTerm);
    }
    private async Task RefreshOpenEditorTabsFromDiskAsync() {
        foreach (var tab in _editorTabs.ToArray()) {
            if (tab.IsRealized)
                await ReloadExistingTabIfChangedAsync(tab);
        }
    }
    private void SetPreviewTab(EditorTab? tab) {
        if (_previewEditorTab is { } old && !ReferenceEquals(old, tab))
            _vm.Tabs.SetEditorTabPreview(old.Id, false);
        _previewEditorTab = tab;
        if (tab is not null) {
            EditorTabNavigationPolicy.MovePreviewTabToEnd(_editorTabs, tab);
            _vm.Tabs.SetEditorTabPreview(tab.Id, true);
        }
    }
}
