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
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        path = Path.GetFullPath(path);
        EnsureEditorPaneForOpenedFile(path);
        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
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
        tab.Control.LoadFile(path);
        UpdateEditorTab(tab);
        RecordTrailEditorTab(tab);
        await UpdateEditorSupportAsync();
        SaveActiveWorkspaceSnapshot();
    }
    private async Task OpenFileInPreviewTabAsync(string path) {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        path = Path.GetFullPath(path);
        EnsureEditorPaneForOpenedFile(path);
        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) {
            ActivateEditorTab(existing.Id);
            await ReloadExistingTabIfChangedAsync(existing);
            return;
        }
        var target = _previewEditorTab is { } preview && _editorTabs.Contains(preview)
                     && !preview.PeekIsModified && !preview.PeekIsVirtual
            ? preview
            : _activeEditorTab is { } active && _editorTabs.Contains(active)
              && string.IsNullOrEmpty(active.PeekFilePath) && !active.PeekIsModified
              && !active.PeekIsVirtual && active.VirtualTitle is null
                ? active
                : null;
        if (target is null) {
            target = CreateEditorTab();
            _editorTabs.Add(target);
            _vm.Tabs.AddEditorTab(target.Id, path, false, false);
        }
        var trailSaved = _trailSuppressed;
        _trailSuppressed = true;
        try { ActivateEditorTab(target.Id); }
        finally { _trailSuppressed = trailSaved; }
        target.Control.LoadFile(path);
        SetPreviewTab(target);
        UpdateEditorTab(target);
        RecordTrailEditorTab(target);
        await UpdateEditorSupportAsync();
        SaveActiveWorkspaceSnapshot();
    }
    private async Task ReloadExistingTabIfChangedAsync(EditorTab tab) {
        var path = tab.Control.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        if (tab.Control.IsModified)
            return;
        string diskText;
        try { diskText = await File.ReadAllTextAsync(path); }
        catch { return; }   // 読めなければ現状維持（best-effort）
        if (NormalizeEol(diskText) != NormalizeEol(tab.Control.Text)) {
            tab.Control.LoadFile(path);
            UpdateEditorTab(tab);
        }
        if (ReferenceEquals(_editorSupport.Source, tab))
            await UpdateEditorSupportAsync();
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
    private static string NormalizeEol(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
    private void SetPreviewTab(EditorTab? tab) {
        if (_previewEditorTab is { } old && !ReferenceEquals(old, tab))
            _vm.Tabs.SetEditorTabPreview(old.Id, false);
        _previewEditorTab = tab;
        if (tab is not null) {
            MovePreviewEditorTabToEnd();
            _vm.Tabs.SetEditorTabPreview(tab.Id, true);
        }
    }
    private void MovePreviewEditorTabToEnd() {
        if (_previewEditorTab is not { } preview)
            return;
        var index = _editorTabs.FindIndex(t => ReferenceEquals(t, preview));
        var last = _editorTabs.Count - 1;
        if (index < 0 || index == last)
            return;
        _editorTabs.RemoveAt(index);
        _editorTabs.Add(preview);
    }
}
