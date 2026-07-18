namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow に残る EditorSupport の View イベント配線。</summary>
public partial class ShellWindow {
    private Task<WebView2CompositionControl?> EnsureEditorSupportViewAsync() => _editorSupport.WebView.EnsureAsync();
    private void RenderPendingEditorSupportContent(CoreWebView2 core) => _editorSupport.WebView.RenderPending(core);
    internal bool TryHorizontalScrollEditorSupportWebView(int delta) => _editorSupport.WebView.TryHorizontalScroll(delta);
    private void PostEditorSupportScrollRatio(double ratio) => _editorSupport.WebView.PostScrollRatio(ratio);
    private async Task OpenEditorSupportSnapshotInBrowserAsync(string html, string? mapFolder, string title) {
        if (!_editorSupportNavigation.TryWritePage(html, out var pageUrl))
            return;
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        var tab = CreateBrowserTab("about:blank", requestedTitle: title);
        await EnsureBrowserRealizedAsync(tab);
        if (tab.View.CoreWebView2 is not { } core)
            return;
        _editorSupportNavigation.ConfigureVirtualHosts(core, mapFolder);
        core.Navigate(pageUrl);
        UpdateBrowserTab(tab);
        SaveActiveWorkspaceSnapshot();
    }
    private void ShowEditorSupportVisual(FrameworkElement view)
        => _editorSupport.ShowVisual(EditorSupportContentHost, view);
    private void HideEditorSupportVisual()
        => _editorSupport.ShowWebView();
    private void EditorSupportVisual_ContentEdited(object? sender, EditorSupportContentEdited e) {
        var tab = _editorSupport.Source;
        if (tab is null
            || !string.Equals(tab.Control.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase))
            return;
        if (tab.Control.Text == e.Text)
            return;
        tab.Control.SetText(e.Text);
    }
    private void ShowEditorSupportPane() {
        if (IsPaneVisible(PaneKind.EditorSupport))
            return;
        EnsureEditorSupportLeafBesideEditor();
        SetPaneVisible(PaneKind.EditorSupport, true);
    }
    private void EnsureEditorSupportLeafBesideEditor() {
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot
            && AllLeaves(savedRoot).All(l => l.Kind != PaneKind.EditorSupport)
            && AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == PaneKind.Editor) is { } savedEditor) {
            _spanSavedRoot = InsertRelative( savedRoot, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, savedEditor, DropZone.Right);
        }
        if (FindLeaf(PaneKind.EditorSupport) is not null)
            return;
        if (FindLeaf(PaneKind.Editor) is not { } editorLeaf)
            return; // Editor がツリーに無い場合は SetPaneVisible の既定動作（最下段へ追加）に任せる
        CaptureLayoutSizes();
        _root = InsertRelative(_root, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, editorLeaf, DropZone.Right);
    }
    private void DetachEditorSupportSource() {
        if (_editorSupport.DetachSource() is { } previous) {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        StopCodeReadyRetry();
    }
    private void EditorSupportSource_ViewportScrolled(object? sender, EventArgs e) {
        if (_syncingEditorFromSupport || sender is not VimEditorControl editor)
            return;
        _editorSupport.WebView.PostScrollRatio(editor.VerticalScrollRatio);
    }
    private void EditorSupport_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        if (_editorSupport.Source is null)
            return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
                return;
            switch (typeElement.GetString()) {
                case "markdownPreviewScroll":
                    if (root.TryGetProperty("ratio", out var ratioElement)
                        && ratioElement.TryGetDouble(out var ratio)) {
                        _syncingEditorFromSupport = true;
                        try { _editorSupport.Source.Control.ScrollToVerticalRatio(ratio); }
                        finally { _syncingEditorFromSupport = false; }
                    }
                    break;
                case "jumpToSource":
                    var line = root.TryGetProperty("line", out var lineElement)
                               && lineElement.TryGetInt32(out var l) ? l : 0;
                    FocusEditorSupportSource(line > 0 ? line : null);
                    break;
                case "linkClicked":
                    if (root.TryGetProperty("href", out var hrefElement) && hrefElement.GetString() is { } href)
                        _ = HandleEditorSupportLinkClickedAsync(href);
                    break;
                case "toggleTaskCheckbox":
                    if (root.TryGetProperty("line", out var taskLineElement) && taskLineElement.TryGetInt32(out var taskLine))
                        ToggleMarkdownTaskCheckbox(taskLine);
                    break;
            }
        } catch {
        }
    }
    private void FocusEditorSupportSource(int? line, bool alignTop = false) {
        var tab = _editorSupport.Source;
        if (tab is null)
            return;
        if (_stageActive && _stagePane != PaneKind.Editor)
            SetStagePane(PaneKind.Editor);
        SetActiveEditorTab(tab);
        if (line is int l) {
            tab.Control.NavigateTo(l - 1, 0);
            if (alignTop)
                tab.Control.ScrollCursorToTop();
        }
        tab.Control.Focus();
        _focusedRegion = FocusTarget.Of(PaneKind.Editor);
    }
    private void EditorSupport_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e) {
        if (_editorSupport.Source is null || sender is not CoreWebView2 core)
            return;
        try {
            for (var i = e.MenuItems.Count - 1; i >= 0; i--) {
                if (e.MenuItems[i].Name is "back" or "forward")
                    e.MenuItems.RemoveAt(i);
            }
            var item = core.Environment.CreateContextMenuItem( "エディタへフォーカス", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => FocusEditorSupportSource(null));
            e.MenuItems.Insert(0, item);
            var back = core.Environment.CreateContextMenuItem( "前のファイルへ戻る", null, CoreWebView2ContextMenuItemKind.Command);
            back.IsEnabled = _editorSupport.History.CanGoBack;
            back.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = EditorSupportGoBackAsync());
            e.MenuItems.Insert(1, back);
        } catch {
        }
    }
}
