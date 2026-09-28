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
        // ワークスペース切替の途中は、タブ集合がまだ前のワークスペースを指している（WorkspaceTransitionGate）。
        // 明示的に開いた要求は捨てず、切替が落ち着いてから新しいワークスペースで開く。
        await _workspaceTransition.WhenSettledAsync();
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
        var epoch = _workspaceTransition.Epoch;
        await LoadEditorFileAsync(tab, path);
        // 読み込みを待つ間に切替が挟まった：タブは開いたワークスペースに残るので、後始末
        // （アクティブ評価・軌跡・EditorSupport・保存）を別のワークスペースへ効かせない。
        if (_workspaceTransition.HasSwitchedSince(epoch))
            return;
        OnActiveEditorFileChanged(tab);   // Activate 時点ではまだパス未設定なので、読み込み後に評価する
        UpdateEditorTab(tab);
        RecordTrailEditorTab(tab);
        InvalidateEditorSupport();
        SaveActiveWorkspaceSnapshot();
    }
    private async Task OpenFileInPreviewTabAsync(string path) {
        // プレビューは「いま選んでいるものを覗く」だけなので、切替の途中に届いたものは捨てる
        // （待って開くと、切替が復元したアクティブタブを前のワークスペースの選択で上書きしてしまう）。
        if (_workspaceTransition.IsSwitching)
            return;
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
        var epoch = _workspaceTransition.Epoch;
        await LoadEditorFileAsync(target, path);
        // 読み込みを待つ間に切替が挟まった：前のワークスペースのタブをプレビュー枠として覚えさせない
        // （新規タブで開くときと同じ理由）。
        if (_workspaceTransition.HasSwitchedSince(epoch))
            return;
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
        if (await ReloadEditorTabsFromDiskAsync(paths))
            _activeEditorTab?.Control.HighlightSearch(highlightTerm);
    }
    /// <summary>ペインの外（検索の一括置換・Diff の右での保存）で書き換わったファイルを開いているタブを
    /// 読み直す。未保存の編集があるタブは読み直さない。アクティブタブが含まれていたかを返す。</summary>
    private async Task<bool> ReloadEditorTabsFromDiskAsync(IReadOnlyList<string> paths) {
        // 呼び手のパスは表記が揃っていない（Diff は git の相対パスを Path.Combine するので
        // `C:\repo\src/a.cs` のように区切りが混ざる）ので、文字列でなくパスとして比べる。
        var activeAffected = false;
        foreach (var tab in _editorTabs.ToArray()) {
            if (!tab.IsRealized || !paths.Any(path => FilePathRelations.AreEqual(tab.PeekFilePath, path)))
                continue;
            await ReloadExistingTabIfChangedAsync(tab);
            if (ReferenceEquals(tab, _activeEditorTab))
                activeAffected = true;
        }
        return activeAffected;
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
