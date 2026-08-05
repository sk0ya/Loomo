using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow に残る EditorSupport の View イベント配線。</summary>
public partial class ShellWindow {
    private Task<WebView2CompositionControl?> EnsureEditorSupportViewAsync() => _editorSupport.WebView.EnsureAsync();
    /// <summary>WebView2 側が行き詰まった（ナビゲーション失敗・応答なし・初回描画の取りこぼし）ときの復帰。
    /// ページ全体を組み直させる——本文差し替えでは、そもそも土台が無いので直らない。</summary>
    private void OnEditorSupportReloadRequested(object? sender, EventArgs e) {
        _editorSupportForceFullPage = true;
        InvalidateEditorSupport();
    }
    internal bool TryHorizontalScrollEditorSupportWebView(int delta) => _editorSupport.WebView.TryHorizontalScroll(delta);
    /// <summary>検索パネルの条件を EditorSupport（プレビュー）のハイライトへ流す。プレビューの一致は
    /// 検索結果一覧に出てこないので、エディタ側だけ塗ると「ヒットしたのにプレビューのどこか分からない」
    /// ままになる。WebView2 表示（本体＋切り離した複製）と、自分で塗るビジュアル表示（VGrid のグリッド等）の
    /// 両方へ配る。表示インスタンスは表示面ごとに <c>EditorSupportVisualHost</c> が持ち、条件を覚えて
    /// <b>あとから作られた実体にも</b>適用するので、ここは各ホストへ1回ずつ渡すだけでよい。</summary>
    private void ApplyEditorSupportSearchHighlight() {
        var search = _vm.SearchPanel;
        var (term, caseSensitive, useRegex) =
            (search.SupportHighlightTerm, search.HighlightCaseSensitive, search.HighlightUseRegex);
        _editorSupport.WebView.SetSearchHighlight(term, caseSensitive, useRegex);
        _editorSupport.Visuals.SetSearchHighlight(term, caseSensitive, useRegex);
        foreach (var mirror in Detached.AllItems.Select(i => i.Content).OfType<DetachedEditorSupportView>())
            mirror.SetSearchHighlight(term, caseSensitive, useRegex);
    }
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
            // ペインに載せた Pochi は type ではなく op（{id, op, …}）で話しかけてくる（ShellWindow.PochiBridge.cs）。
            if (root.TryGetProperty("op", out var opElement) && sender is CoreWebView2 bridgeCore) {
                HandlePochiBridgeMessage(bridgeCore, root, opElement.GetString());
                return;
            }
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
    private void FocusEditorSupportSource(int? line, int column0 = 0, bool alignTop = false) {
        var tab = _editorSupport.Source;
        if (tab is null)
            return;
        if (_stageActive && _stagePane != PaneKind.Editor)
            SetStagePane(PaneKind.Editor);
        SetActiveEditorTab(tab);
        if (line is int l) {
            tab.Control.NavigateTo(l - 1, Math.Max(0, column0));
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
            EditorSupportContextLink.RemoveBuiltInOpenInNewWindow(e.MenuItems);
            var item = core.Environment.CreateContextMenuItem( "エディタへフォーカス", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => FocusEditorSupportSource(null));
            e.MenuItems.Insert(0, item);
            var back = core.Environment.CreateContextMenuItem( "前のファイルへ戻る", null, CoreWebView2ContextMenuItemKind.Command);
            back.IsEnabled = _editorSupport.History.CanGoBack;
            back.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = EditorSupportGoBackAsync());
            e.MenuItems.Insert(1, back);
            // リンク上での右クリックか（＝生 href の取得）は非同期でしか分からないので、メニュー表示を待たせる。
            _ = AddEditorSupportLinkMenuItemAsync(core, e, e.GetDeferral(), _editorSupport.Source?.Control.FilePath);
        } catch {
        }
    }
    /// <summary>右クリックがリンク上なら「別ウィンドウで開く」を足す。宛先の振り分け（URL＝ブラウザ／
    /// ファイル＝エディタ）はクリック時（<see cref="HandleEditorSupportLinkClickedAsync"/>）と同じ解決を使う。</summary>
    private async Task AddEditorSupportLinkMenuItemAsync( CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e, CoreWebView2Deferral deferral, string? sourcePath) {
        try {
            var href = await EditorSupportContextLink.ReadHrefAsync(core);
            var target = LinkOpenTargetResolver.Resolve(_workspace, href, sourcePath);
            if (DescribeOpenLinkInWindow(target) is not { } header)
                return;
            var item = core.Environment.CreateContextMenuItem(header, null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => OpenLinkTargetInDetachedWindow(target));
            e.MenuItems.Insert(0, item);
        } catch {
            // メニューを組めなくても表示は続ける（deferral は必ず完了させる）。
        } finally {
            deferral.Complete();
        }
    }
}
