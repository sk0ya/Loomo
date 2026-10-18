using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow に残る EditorSupport の View イベント配線。</summary>
public partial class ShellWindow {
    private Task<WebView2CompositionControl?> EnsureEditorSupportViewAsync() => _editorSupport.WebView.EnsureAsync();
    /// <summary>WebView2 側が行き詰まった（ナビゲーション失敗・応答なし・初回描画の取りこぼし）ときの復帰。
    /// ページ全体を組み直させる——本文差し替えでは、そもそも土台が無いので直らない。</summary>
    private void OnEditorSupportReloadRequested(object? sender, EventArgs e) {
        CodeSupportDiag.Log($"editor support: 組み直し要求を受理（描画可={CanRenderEditorSupport()}）");
        _editorSupportForceFullPage = true;
        InvalidateEditorSupport();
    }
    /// <summary>
    /// 利用者の「作り直す」（ヘッダーの ↻・右クリック・コマンドパレット）。<b>自動の復帰が諦めた後の逃げ道</b>で、
    /// 自動側は無限に回らないよう歯止めを持つ（同じページの二度目の失敗は追わない・WebView2 の再試行は3回・
    /// 描画の見限りは中断を伝えてから5秒）——歯止めに当たると、ペインを閉じて開き直しても中身は戻らなかった。
    /// <para>
    /// 捨てるのは表示の側だけ：走行中の描画と持ち越した要求、WebView2（ページの記憶ごと）、ビジュアルの実体、
    /// 取ってあった構造。追従元・ピン・ファイル履歴は残すので、同じファイルが白紙から描き直される。
    /// 走行中の描画が中断に応じなくても、更新ループが見限った時点で（最長5秒）新しい描画が走る。
    /// </para>
    /// </summary>
    private void RebuildEditorSupport() {
        CodeSupportDiag.Log("editor support: 利用者の求めで作り直す");
        _editorSupportDebounceTimer?.Stop();
        _editorSupportLoop?.Restart();   // 先に走行中の描画を止める（捨てる WebView2 へ書かせない）
        _editorSupport.ResetForRebuild(EditorSupportContentHost);
        _editorSupport.WebView.Rebuild();   // 検索ハイライトの条件はどちらも保持しているので、作り直した先にも塗られる
        _editorSupportForceFullPage = true;
        InvalidateEditorSupport();
    }
    private void OnRebuildEditorSupport(object sender, RoutedEventArgs e) => RebuildEditorSupport();
    /// <summary>プレビュー中のファイルをディスク上で見張る監視（§24.8）。<b>常に1ファイルぶんだけ</b>で、
    /// 対象が変われば張り替え、見張る理由が無くなれば止める。</summary>
    private SingleFileWatcher? _editorSupportFileWatcher;
    private SingleFileWatcher EditorSupportFileWatcher
        => _editorSupportFileWatcher ??= new SingleFileWatcher(OnEditorSupportFileChanged);
    /// <summary>監視をいまの状態（追従元のファイル・提供者・ペインの見え方）へ合わせる。冪等なので、
    /// 状態が変わりうる場所（<see cref="InvalidateEditorSupport"/>・レイアウト再構築・追従元の切り離し）から
    /// 素直に呼んでよい。判断は毎回 <see cref="EditorSupportAutoReload.WatchTarget"/> から取り直す。</summary>
    private void UpdateEditorSupportFileWatch() {
        var filePath = _editorSupport.Source?.PeekFilePath;   // タブを実体化させない読み方
        var provider = string.IsNullOrEmpty(filePath) ? null : _editorSupportResolver.Resolve(filePath).Provider;
        var target = EditorSupportAutoReload.WatchTarget(provider, filePath, CanRenderEditorSupport());
        if (target is null && _editorSupportFileWatcher is null)
            return;   // 見張るものが無く、まだ張ってもいない：監視そのものを作らない
        EditorSupportFileWatcher.Watch(target);
    }
    /// <summary>プレビュー中のファイルがディスク上で変わった（通知は UI スレッドへ渡り済み・デバウンス済み）。
    /// <b>いま実際にそのファイルを出しているときだけ</b>読み直す——監視の発火から通知までの間に追従先が
    /// 変わっていることがあるので、条件はここで取り直す。</summary>
    private void OnEditorSupportFileChanged(string changedPath) {
        var filePath = _editorSupport.Source?.PeekFilePath;
        var provider = string.IsNullOrEmpty(filePath) ? null : _editorSupportResolver.Resolve(filePath).Provider;
        if (!EditorSupportAutoReload.ShouldReload(changedPath, provider, filePath, CanRenderEditorSupport())
            || provider is not IEditorSupportUriProvider uriProvider)
            return;
        CodeSupportDiag.Log($"editor support: ファイル更新を検知して読み直す（{Path.GetFileName(changedPath)}）");
        if (_editorSupport.WebView.ReloadShowing(uriProvider.ResolveNavigationUri(filePath!)))
            return;
        // そのページを読み終えて載せてはいない（読み込み中・失敗後・別ページ）。読み直しでは直らないので
        // 通常の描画経路でページごと組み直す（同じページの二度目の失敗はページ状態機械が止める）。
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
    /// <summary>検索結果の一致へ移る（開いた後に呼ぶ）。文書の一致は Line が行ではない（ページ・シート等）ので、
    /// エディタではなく EditorSupport をそこへ合わせる——PDF はそのページ（語は <see cref="ApplyEditorSupportSearchHighlight"/>
    /// の Find が塗る）、Excel はそのシート・セル。Word・PowerPoint は開くだけ（Word のプレビューは語が塗られる）。</summary>
    private void RevealSearchHit(SearchHit hit) {
        if (SearchDocuments.IsDocument(hit.FullPath)) {
            if (SearchDocuments.IsPdf(hit.FullPath)) {
                if (_editorSupportResolver.Resolve(hit.FullPath).Provider is IEditorSupportUriProvider uriProvider)
                    _editorSupport.WebView.ShowPdfPage(uriProvider.ResolveNavigationUri(hit.FullPath), hit.Line);
            } else {
                _editorSupport.Visuals.RevealLocation(hit.FullPath, hit.Line, hit.Location);
            }
            // 切り離した複製も、そのファイルを出していれば同じ位置へ合わせる。
            foreach (var mirror in Detached.AllItems.Select(i => i.Content).OfType<DetachedEditorSupportView>())
                mirror.RevealDocumentLocation(hit.FullPath, hit.Line, hit.Location);
            return;
        }
        _activeEditorTab?.Control.NavigateTo(hit.Line - 1, Math.Max(0, hit.Column - 1));
        _activeEditorTab?.Control.HighlightSearch(hit.Highlight);
    }
    private void PostEditorSupportScrollRatio(double ratio) => _editorSupport.WebView.PostScrollRatio(ratio);
    private async Task OpenEditorSupportSnapshotInBrowserAsync(string html, string? mapFolder, string title) {
        if (!_editorSupportNavigation.TryWritePage(html, out var pageUrl))
            return;
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        var tab = CreateBrowserTab("about:blank", requestedTitle: title);
        await EnsureBrowserRealizedAsync(tab);
        if (tab.View.TryCore() is not { } core)
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
            _spanSavedRoot = PaneLayoutTree.InsertRelative(savedRoot, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, savedEditor, DropZone.Right);
        }
        if (FindLeaf(PaneKind.EditorSupport) is not null)
            return;
        if (FindLeaf(PaneKind.Editor) is not { } editorLeaf)
            return; // Editor がツリーに無い場合は SetPaneVisible の既定動作（最下段へ追加）に任せる
        CaptureLayoutSizes();
        _root = PaneLayoutTree.InsertRelative(_root, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, editorLeaf, DropZone.Right);
    }
    private void DetachEditorSupportSource() {
        _editorSupportFileWatcher?.Stop();   // 追従元がいなくなる＝見張る理由も無くなる（§24.8）
        if (_editorSupport.DetachSource() is { } previous) {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        StopCodeReadyRetry();
    }
    /// <summary>ワークスペース切替。追従元を外すだけでなく、描きかけの描画・持ち越した要求・
    /// 追従元に紐づく状態（構造・履歴・準備待ち）・ページの記憶をまとめて捨てる。
    /// 判断の中身は <see cref="EditorSupportUpdateLoop.Restart"/> と
    /// <see cref="EditorSupportController.ResetForWorkspaceSwitch"/> にあり、どちらもテストで固定してある。</summary>
    private void ResetEditorSupportForWorkspaceSwitch() {
        _editorSupportDebounceTimer?.Stop();
        _editorSupportFileWatcher?.Stop();
        _editorSupportLoop?.Restart();   // 追従元を外す前に、前のファイルを描いている描画を止める
        if (_editorSupport.ResetForWorkspaceSwitch() is { } previous) {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        _editorSupport.WebView.ResetPageState();
        _editorSupportForceFullPage = false;
        // 編集モードは WebView controller が所有する。次の .md に編集状態を持ち越さない。
        _editorSupport.WebView.SetMarkdownEditMode(false);
        UpdateEditorSupportPinToggle();
        UpdateEditorSupportNavAffordances();
    }
    private void EditorSupportSource_ViewportScrolled(object? sender, EventArgs e) {
        if (_syncingEditorFromSupport || sender is not VimEditorControl editor)
            return;
        _editorSupport.WebView.PostScrollRatio(editor.VerticalScrollRatio);
    }
    private EditorSupportWebMessageDispatcher? _editorSupportMessageDispatcher;
    private EditorSupportWebMessageDispatcher EditorSupportMessageDispatcher
        => _editorSupportMessageDispatcher ??= new EditorSupportWebMessageDispatcher(
            HandlePochiBridgeMessage,
            ScrollEditorSupportSourceToRatio,
            line => FocusEditorSupportSource(line),
            href => _ = HandleEditorSupportLinkClickedAsync(href),
            ToggleMarkdownTaskCheckbox,
            ApplyMarkdownPreviewEdit);
    private void EditorSupport_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        => EditorSupportMessageDispatcher.Dispatch(
            e.WebMessageAsJson, sender, sourceAvailable: _editorSupport.Source is not null);
    private void ScrollEditorSupportSourceToRatio(double ratio) {
        if (_editorSupport.Source is not { } source)
            return;
        _syncingEditorFromSupport = true;
        try { source.Control.ScrollToVerticalRatio(ratio); }
        finally { _syncingEditorFromSupport = false; }
    }
    private void OnToggleEditorSupportEdit(object sender, RoutedEventArgs e) {
        EditorSupportFramePresenter.ToggleMarkdownEditMode();
    }
    private void ApplyMarkdownPreviewEdit(string text) {
        var tab = _editorSupport.Source;
        if (tab?.Control.FilePath is not { } filePath
            || _editorSupportResolver.Resolve(filePath).Provider is not MarkdownEditorSupport
            || string.Equals(tab.Control.Text, text, StringComparison.Ordinal))
            return;
        tab.Control.SetText(text);
    }
    private void FocusEditorSupportSource(int? line, int column0 = 0, bool alignTop = false) {
        var tab = _editorSupport.Source;
        if (tab is null)
            return;
        if (_stageActive && _stagePane != PaneKind.Editor)
            SetStagePane(PaneKind.Editor);
        else if (_dockActive)
            EnsureDockPaneShown(PaneKind.Editor);   // 中央にも下／右にも置ける（既定は中央）
        SetActiveEditorTab(tab);
        if (line is int l) {
            tab.Control.NavigateTo(l - 1, Math.Max(0, column0));
            if (alignTop)
                tab.Control.ScrollCursorToTop();
        }
        tab.Control.Focus();
        _focusedRegion = PaneFocusTarget.Of(PaneKind.Editor);
    }
    private void EditorSupport_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e) {
        if (_editorSupport.Source is null || sender is not CoreWebView2 core)
            return;
        try {
            for (var i = e.MenuItems.Count - 1; i >= 0; i--) {
                if (e.MenuItems[i].Name is "back" or "forward")
                    e.MenuItems.RemoveAt(i);
            }
            EditorSupportContextLinkBridge.RemoveBuiltInOpenInNewWindow(e.MenuItems);
            var item = core.Environment.CreateContextMenuItem( "エディタへフォーカス", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => FocusEditorSupportSource(null));
            e.MenuItems.Insert(0, item);
            var back = core.Environment.CreateContextMenuItem( "前のファイルへ戻る", null, CoreWebView2ContextMenuItemKind.Command);
            back.IsEnabled = _editorSupport.History.CanGoBack;
            back.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = EditorSupportGoBackAsync());
            e.MenuItems.Insert(1, back);
            e.MenuItems.Add(core.Environment.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            var rebuild = core.Environment.CreateContextMenuItem("表示を作り直す（固まったとき）", null, CoreWebView2ContextMenuItemKind.Command);
            rebuild.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => RebuildEditorSupport());
            e.MenuItems.Add(rebuild);
            // リンク上での右クリックか（＝生 href の取得）は非同期でしか分からないので、メニュー表示を待たせる。
            _ = AddEditorSupportLinkMenuItemAsync(core, e, e.GetDeferral(), _editorSupport.Source?.Control.FilePath);
        } catch {
        }
    }
    /// <summary>右クリックがリンク上なら「別ウィンドウで開く」を足す。宛先の振り分け（URL＝ブラウザ／
    /// ファイル＝エディタ）はクリック時（<see cref="HandleEditorSupportLinkClickedAsync"/>）と同じ解決を使う。</summary>
    private async Task AddEditorSupportLinkMenuItemAsync( CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e, CoreWebView2Deferral deferral, string? sourcePath) {
        try {
            var href = await EditorSupportContextLinkBridge.ReadHrefAsync(core);
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
