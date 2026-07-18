namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン項目の別ウィンドウ切り離し。Editor は同一ファイルの複製＋双方向テキスト同期、 Terminal/Browser は同期なしの新規スピンオフ。ウィンドウ管理・タブ結合は <see cref="DetachedWindowManager"/>。 状態はワークスペースのスナップショットへ保存し、切替・再起動時に復元する。</summary>
public partial class ShellWindow {
    private DetachedWindowManager? _detached;
    private DetachedWindowManager Detached => _detached ??= new DetachedWindowManager(this, () => SaveActiveWorkspaceSnapshot());
    private DetachedItemSnapshot? CaptureDetachedItem(DetachedItem item) {
        var snapshot = new DetachedItemSnapshot { Kind = item.Kind.ToString() };
        switch (item.Content) {
            case VimEditorControl editor:
                snapshot.FilePath = editor.FilePath;
                snapshot.Text = editor.IsModified || string.IsNullOrWhiteSpace(editor.FilePath) ? editor.Text : null;
                snapshot.IsModified = editor.IsModified;
                break;
            case TerminalTabView terminal:
                snapshot.WorkingDirectory = terminal.WorkingDirectory;
                break;
            case WebView2CompositionControl browser:
                snapshot.Url = browser.Source?.ToString();
                break;
            case DetachedEditorSupportView preview:
                snapshot.FilePath = preview.SourceFilePath;
                break;
            default:
                return null;
        }
        return snapshot;
    }
    private DetachedItem? RestoreDetachedItem(DetachedItemSnapshot snapshot) {
        if (!Enum.TryParse<DetachKind>(snapshot.Kind, out var kind)) return null;
        if (kind == DetachKind.EditorMirror && !string.IsNullOrWhiteSpace(snapshot.FilePath)) {
            var source = _editorTabs.FirstOrDefault(t => string.Equals( t.PeekFilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase));
            if (source is not null) return TryCreateEditorMirrorItem(source.Id);
        }
        if (kind is DetachKind.EditorMirror or DetachKind.EditorMove) {
            var editor = CreateEditorTab().Control;
            if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
                editor.LoadFile(snapshot.FilePath);
            if (snapshot.Text is not null) editor.SetText(snapshot.Text);
            var title = string.IsNullOrWhiteSpace(snapshot.FilePath) ? "Untitled" : Path.GetFileName(snapshot.FilePath);
            return new DetachedItem(DetachKind.EditorMove, title, editor, _tabIcons.GetFileIcon(snapshot.FilePath), editor.Dispose);
        }
        if (kind == DetachKind.EditorSupportMirror && !string.IsNullOrWhiteSpace(snapshot.FilePath)) {
            var source = _editorTabs.FirstOrDefault(t => string.Equals( t.PeekFilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase));
            if (source is null) return null;
            var view = new DetachedEditorSupportView(_editorSupports, _editorSupport.Pipeline, _settings, _workspace.RootPath, source.Control);
            var item = new DetachedItem(kind, $"Preview: {Path.GetFileName(snapshot.FilePath)}", view, dispose: view.Dispose);
            view.TitleChanged += (_, title) => item.Title = title;
            AttachEditorSupportMirrorLinks(view);
            return item;
        }
        if (kind is DetachKind.TerminalSpinoff or DetachKind.TerminalMove)
            return CreateTerminalSpinoffItem(snapshot.WorkingDirectory);
        if (kind == DetachKind.BrowserSpinoff)
            return CreateBrowserSpinoffItem(snapshot.Url);
        return null;
    }
    private void OnSidebarTabDetachRequested(object? sender, TabEntryViewModel tab) {
        DetachedItem? item = tab.Kind switch {
            TabEntryKind.Editor => TryCreateEditorMirrorItem(tab.Id), TabEntryKind.Terminal => CreateTerminalSpinoffItem(_terminalTabs.FirstOrDefault(t => t.Id == tab.Id)), TabEntryKind.Browser => CreateBrowserSpinoffItem(_browserTabs.FirstOrDefault(t => t.Id == tab.Id)), _ => null
        };
        if (item is not null)
            Detached.Detach(item);
    }
    private void OnDetachEditorPane(object sender, RoutedEventArgs e) {
        var id = _editorViews?.FocusedTabId ?? _activeEditorTab?.Id;
        if (id is { } tabId && TryCreateEditorMirrorItem(tabId) is { } item)
            Detached.Detach(item);
    }
    private void OnDetachTerminalPane(object sender, RoutedEventArgs e) {
        var src = _terminalViews?.FocusedTabId is { } id
            ? _terminalTabs.FirstOrDefault(t => t.Id == id)
            : _activeTerminalTab;
        Detached.Detach(CreateTerminalSpinoffItem(src));
    }
    private void OnDetachBrowserPane(object sender, RoutedEventArgs e)
        => Detached.Detach(CreateBrowserSpinoffItem(_activeBrowserTab));
    private void OnDetachEditorSupport(object sender, RoutedEventArgs e) {
        var source = (_editorSupport.Source ?? _activeEditorTab)?.Control;
        if (source is null)
            return;
        var view = new DetachedEditorSupportView(_editorSupports, _editorSupport.Pipeline, _settings, _workspace.RootPath, source);
        var title = string.IsNullOrWhiteSpace(source.FilePath)
            ? "Preview"
            : $"Preview: {Path.GetFileName(source.FilePath!)}";
        var item = new DetachedItem( DetachKind.EditorSupportMirror, title, view, dispose: view.Dispose);
        view.TitleChanged += (_, t) => item.Title = t;
        AttachEditorSupportMirrorLinks(view);
        Detached.Detach(item);
    }
    private void AttachEditorSupportMirrorLinks(DetachedEditorSupportView view)
        => view.LinkClicked += async (_, href) => {
            await HandleEditorSupportLinkClickedAsync(href, view.SourceFilePath);
            Activate();
        };
    private DetachedItem? TryCreateEditorMirrorItem(Guid sourceTabId) {
        var src = _editorTabs.FirstOrDefault(t => t.Id == sourceTabId);
        if (src is null)
            return null;
        var srcCtl = src.Control;                 // 未実体化なら実体化
        var mirror = CreateEditorTab().Control;    // 独立コントロール（_editorTabs には加えない＝非永続）
        if (!string.IsNullOrWhiteSpace(srcCtl.FilePath) && File.Exists(srcCtl.FilePath) && !srcCtl.IsModified)
            mirror.LoadFile(srcCtl.FilePath);
        else
            mirror.SetText(srcCtl.Text);
        var syncing = false;
        void Sync(VimEditorControl from, VimEditorControl to) {
            if (syncing || string.Equals(to.Text, from.Text, StringComparison.Ordinal))
                return;
            syncing = true;
            try {
                var caret = to.Caret;
                to.SetText(from.Text);
                try { to.NavigateTo(caret.Line, caret.Column); } catch { /* 縮んだ本文で範囲外なら内部でクランプ */ }
            } finally { syncing = false; }
        }
        EventHandler srcHandler = (_, _) => Sync(srcCtl, mirror);
        EventHandler mirHandler = (_, _) => Sync(mirror, srcCtl);
        srcCtl.BufferChanged += srcHandler;
        mirror.BufferChanged += mirHandler;
        var title = string.IsNullOrWhiteSpace(srcCtl.FilePath) ? "Untitled" : Path.GetFileName(srcCtl.FilePath!);
        return new DetachedItem( DetachKind.EditorMirror, title, mirror, _tabIcons.GetFileIcon(srcCtl.FilePath), dispose: () => {
                srcCtl.BufferChanged -= srcHandler;
                mirror.BufferChanged -= mirHandler;
                mirror.Dispose();
            });
    }
    private DetachedItem CreateTerminalSpinoffItem(TerminalTab? sourceTab)
        => CreateTerminalSpinoffItem(sourceTab?.View.WorkingDirectory);
    private DetachedItem CreateTerminalSpinoffItem(string? sourceDirectory) {
        var cwd = sourceDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
        var view = new TerminalTabView("pwsh.exe", cwd) { AutoFocusOnStart = false };
        _appearance.ApplyTerminalAppearance(view);
        var item = new DetachedItem( DetachKind.TerminalSpinoff, "Terminal", view, _tabIcons.GetTerminalIcon(), dispose: () => _ = view.CloseAsync());
        view.HeaderTitleChanged += (_, title) =>
            item.Title = string.IsNullOrWhiteSpace(title) ? "Terminal" : title;
        return item;
    }
    private DetachedItem CreateBrowserSpinoffItem(BrowserTab? sourceTab)
        => CreateBrowserSpinoffItem(sourceTab?.View.Source?.ToString() ?? sourceTab?.PendingUrl);
    private DetachedItem CreateBrowserSpinoffItem(string? sourceUrl) {
        var url = sourceUrl ?? DefaultBrowserUrl;
        var view = new WebView2CompositionControl {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E), CreationProperties = CreateWebViewCreationProperties()
        };
        var item = new DetachedItem( DetachKind.BrowserSpinoff, "Browser", view, _tabIcons.GetBrowserDefaultIcon(), dispose: () => view.Dispose());
        _ = RealizeSpinoffBrowserAsync(view, url, item);
        return item;
    }
    private async Task RealizeSpinoffBrowserAsync(WebView2CompositionControl view, string url, DetachedItem item) {
        try { await view.EnsureCoreWebView2Async(); }
        catch { return; }
        ConfigureBrowserCore(view.CoreWebView2!);
        view.CoreWebView2!.DocumentTitleChanged += (_, _) => {
            var title = view.CoreWebView2?.DocumentTitle;
            item.Title = string.IsNullOrWhiteSpace(title) ? "Browser" : title!;
        };
        try { view.Source = new Uri(WorkspaceSessionCoordinator.NormalizeBrowserAddress(url, DefaultBrowserUrl)); }
        catch { /* 不正 URL は無視（空ページのまま） */ }
    }
    private Point _paneTabDragStart;
    private Guid _paneTabDragId;
    private bool _paneTabDragArmed;
    private void OnPaneTabPreviewMouseDown(object sender, MouseButtonEventArgs e) {
        _paneTabDragArmed = false;
        if (ResolvePaneTabId(e.OriginalSource) is { } id) {
            _paneTabDragStart = e.GetPosition(this);
            _paneTabDragId = id;
            _paneTabDragArmed = true;
        }
    }
    private void OnPaneTabPreviewMouseMove(object sender, MouseEventArgs e) {
        if (!_paneTabDragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _paneTabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _paneTabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _paneTabDragArmed = false;
        StartPaneTabTearOff(_paneTabDragId, sender as UIElement);
    }
    private void StartPaneTabTearOff(Guid id, UIElement? source) {
        if (source is null || BuildTearOffFactory(id) is not { } factory)
            return;
        if (Mouse.Captured is not null)
            Mouse.Capture(null);
        Detached.BeginExternalDrag(factory);
        QueryContinueDragEventHandler onQcd = (_, e) => { if (e.EscapePressed) Detached.CancelDrag(); };
        source.QueryContinueDrag += onQcd;
        try {
            var data = new DataObject(DetachedPaneWindow.DetachDragFormat, "external");
            var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
            Detached.EndDrag(result);
        } finally {
            source.QueryContinueDrag -= onQcd;
            Detached.ClearDrag();
        }
    }
    private Func<DetachedItem>? BuildTearOffFactory(Guid id) {
        if (_editorTabs.Any(t => t.Id == id))
            return () => {
                var control = RemoveEditorTabForMove(id)!;
                var title = string.IsNullOrWhiteSpace(control.FilePath) ? "Untitled" : Path.GetFileName(control.FilePath!);
                return new DetachedItem( DetachKind.EditorMove, title, control, _tabIcons.GetFileIcon(control.FilePath), dispose: control.Dispose);
            };
        if (_terminalTabs.Any(t => t.Id == id))
            return () => {
                var view = RemoveTerminalTabForMove(id)!;
                var item = new DetachedItem( DetachKind.TerminalMove, string.IsNullOrWhiteSpace(view.HeaderTitle) ? "Terminal" : view.HeaderTitle, view, _tabIcons.GetTerminalIcon(), dispose: () => _ = view.CloseAsync());
                view.HeaderTitleChanged += (_, t) => item.Title = string.IsNullOrWhiteSpace(t) ? "Terminal" : t;
                return item;
            };
        if (_browserTabs.Any(t => t.Id == id))
            return () => {
                var srcTab = _browserTabs.FirstOrDefault(t => t.Id == id);
                var item = CreateBrowserSpinoffItem(srcTab);   // 同 URL で新規 WebView2（再ペアレント空表示回避）
                if (srcTab is not null)
                    _ = CloseBrowserTabAsync(id);              // メインから元タブを除去＝移動
                return item;
            };
        return null;
    }
    private static Guid? ResolvePaneTabId(object originalSource) {
        for (var d = originalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: Guid id })
                return id;
        return null;
    }
    private VimEditorControl? RemoveEditorTabForMove(Guid id) {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return null;
        var tab = _editorTabs[index];
        var control = tab.Control;   // 未実体化なら実体化（生きたコントロールを移すため）
        var wasActive = _activeEditorTab?.Id == id;
        if (ReferenceEquals(_editorSupport.Source, tab)) {
            _editorSupportDebounceTimer?.Stop();
            DetachEditorSupportSource();
            _editorSupport.IsPinned = false;
            UpdateEditorSupportPinToggle();
        }
        ViewportTree.Detach(control);   // 視覚ツリーから外す（Dispose はしない＝別窓へ移す）
        if (ReferenceEquals(_previewEditorTab, tab))
            _previewEditorTab = null;
        _editorTabs.RemoveAt(index);
        _vm.Tabs.RemoveEditorTab(id);
        _editorViews?.RemoveTab(id);
        if (_editorTabs.Count == 0) {
            var newTab = CreateEditorTab();
            _editorTabs.Add(newTab);
            _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
            ActivateEditorTab(newTab.Id);
        } else {
            _editorViews?.RepairTabs(_editorTabs.Select(t => t.Id));
            if (wasActive)
                ActivateEditorTab(_editorTabs[Math.Min(index, _editorTabs.Count - 1)].Id);
            else {
                _editorViews?.Rebuild();
                if (_editorViews?.FocusedTabId is { } fid && _editorTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                    SetActiveEditorTab(ft);
            }
        }
        SaveActiveWorkspaceSnapshot();
        return control;
    }
    private TerminalTabView? RemoveTerminalTabForMove(Guid id) {
        var index = _terminalTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return null;
        var tab = _terminalTabs[index];
        var wasActive = _activeTerminalTab?.Id == id;
        ViewportTree.Detach(tab.View);   // 視覚ツリーから外す（CloseAsync はしない＝別窓へ移す）
        _terminalTabs.RemoveAt(index);
        _vm.Tabs.RemoveTerminalTab(id);
        _terminalViews?.RemoveTab(id);
        ForgetTerminalActivity(id);
        if (_terminalTabs.Count == 0) {
            var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
            var newTab = CreateTerminalTab(startDir);
            _terminalTabs.Add(newTab);
            _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
            ActivateTerminalTab(newTab.Id);
        } else {
            _terminalViews?.RepairTabs(_terminalTabs.Select(t => t.Id));
            if (wasActive)
                ActivateTerminalTab(_terminalTabs[Math.Min(index, _terminalTabs.Count - 1)].Id);
            else {
                _terminalViews?.Rebuild();
                if (_terminalViews?.FocusedTabId is { } fid && _terminalTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                    SetActiveTerminalTab(ft);
            }
        }
        SaveActiveWorkspaceSnapshot();
        return tab.View;
    }
}
