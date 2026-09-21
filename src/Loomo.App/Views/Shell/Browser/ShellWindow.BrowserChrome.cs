namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ブラウザペインの「道具としての手触り」——ツールバーの状態、アドレス欄の候補、
/// ブックマークと履歴、ページ内検索、ズーム、ダウンロード、キーボード、そして右クリックからの
/// <b>素材の流れ</b>（設計書 §23.3 の共通語彙「〜へ送る」）。
///
/// <para>タブの生成・切替・遷移そのものは <c>ShellWindow.Browser.cs</c> 側にある。</para></summary>
public partial class ShellWindow {
    private BrowserFindController? _browserFindController;
    private BrowserDownloadController? _browserDownloadController;
    private BrowserFindController BrowserFind
        => _browserFindController ??= new BrowserFindController(
            _vm.Browser,
            Dispatcher,
            () => ActiveBrowserView.TryCore(),
            BrowserFindBox,
            () => EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser),
            () => ActiveBrowserView?.Focus());
    private BrowserDownloadController BrowserDownloads
        => _browserDownloadController ??= new BrowserDownloadController(_vm.Browser, Dispatcher);
    private BrowserAddressSuggestionController? _browserAddressSuggestions;
    private BrowserAddressSuggestionController BrowserAddressSuggestions
        => _browserAddressSuggestions ??= new BrowserAddressSuggestionController(
            _vm.Browser,
            BrowserAddressBox,
            BrowserSuggestList,
            () => BrowserDisplayMapper.CurrentUrl(
                _activeBrowserTab?.View.TryUrl(), _activeBrowserTab?.PendingUrl),
            NavigateBrowser);

    private void InitializeBrowserChrome() {
        var vm = _vm.Browser;
        vm.OpenUrlRequested += (_, request) => _ = OpenBrowserLibraryUrlAsync(request.Url, request.NewTab);
        vm.OpenFileInEditorRequested += (_, path) => _ = OpenFileInNewEditorTabAsync(path);
        vm.FindChanged += (_, _) => _ = BrowserFind.ApplyAsync();
        vm.FindStepRequested += (_, step) => BrowserFind.Step(step);
        // ツールバーのドロップダウンは押し直しで閉じたい。閉じた時刻を覚えておかないと
        // 「押し下げで閉じる→Click で開き直す」でトグルにならない（OnBrowser*Toggle）。
        TrackPopupClose(BrowserDownloadsPopup);
        TrackPopupClose(BrowserLibraryPopup);
        TrackPopupClose(BrowserHistoryPopup);
        TrackPopupClose(BrowserExtensionsPopup);
        TrackPopupClose(BrowserPasswordsPopup);
        InitializeBrowserExtras();
    }

    // ── ツールバーのドロップダウン（ダウンロード・ブックマーク・履歴・拡張機能・パスワード） ──
    // 開けるのは ToggleButton の素の動き。ここは「開いている最中に押したら閉じる」だけを受け持つ
    // （なぜマウスアップで受けるかは SuppressPopupReopen の説明にある）。
    private void OnBrowserDownloadsToggle(object sender, MouseButtonEventArgs e) => SuppressPopupReopen(sender, e, BrowserDownloadsPopup);
    private void OnBrowserLibraryToggle(object sender, MouseButtonEventArgs e) {
        _vm.Browser.IsHistoryOpen = false;
        SuppressPopupReopen(sender, e, BrowserLibraryPopup);
    }
    private void OnBrowserHistoryToggle(object sender, MouseButtonEventArgs e) {
        _vm.Browser.IsLibraryOpen = false;
        SuppressPopupReopen(sender, e, BrowserHistoryPopup);
    }
    private void OnBrowserExtensionsToggle(object sender, MouseButtonEventArgs e) => SuppressPopupReopen(sender, e, BrowserExtensionsPopup);
    private void OnBrowserPasswordsToggle(object sender, MouseButtonEventArgs e) => SuppressPopupReopen(sender, e, BrowserPasswordsPopup);

    /// <summary>ブックマークバーの « » と右クリックから、ブックマーク一覧（🔖）を開く。
    /// 帯に入り切らない項目へ辿る道はここ1本にしてある（帯そのものは横スクロールしない）。</summary>
    private void OnBrowserBookmarkBarOverflow(object sender, RoutedEventArgs e) {
        _vm.Browser.IsHistoryOpen = false;
        _vm.Browser.IsLibraryOpen = true;
    }

    /// <summary>帯から落ちた一枚の項目にカーソルが来た。開け閉ての規則そのものは VM 側
    /// （<see cref="BrowserBookmarkMenuItemViewModel.Enter"/>）に置いてあるので、ここは伝えるだけ
    /// ——同じ段の開いている一枚を畳み、フォルダーなら横へもう一枚開く。</summary>
    private void OnBrowserBookmarkMenuItemEnter(object sender, MouseEventArgs e) {
        if (sender is FrameworkElement { DataContext: BrowserBookmarkMenuItemViewModel item })
            item.Enter();
    }

    /// <summary>帯から落ちた一枚の項目を押した。リンクは開き、フォルダーは横へ開く
    /// （カーソルで開くのと同じ動き——触れずに押した人にも同じ結果を返す）。</summary>
    private void OnBrowserBookmarkMenuItemClick(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { DataContext: BrowserBookmarkMenuItemViewModel item })
            return;
        if (item.IsFolder)
            item.Enter();
        else
            _vm.Browser.OpenBookmarkMenuItemCommand.Execute(item);
    }

    /// <summary>取り込みを開く（§21.5.4）。入口が2つ（ブックマーク一覧と鍵の一覧）あるのは、
    /// 取り込む中身がその両方に跨がるから。<b>開く前に呼び出し元のポップアップを閉じる</b>——
    /// どちらも <c>StaysOpen="False"</c> なので、重なったまま出すと下の1枚が居座って操作を食う。</summary>
    private void OnBrowserImportOpen(object sender, RoutedEventArgs e) {
        _vm.Browser.IsLibraryOpen = false;
        _vm.Browser.IsHistoryOpen = false;
        _vm.Browser.IsPasswordsOpen = false;
        _vm.Browser.Import.IsOpen = true;
    }

    // ── アドレス欄 ─────────────────────────────────────────────────────
    /// <summary>アドレス欄へフォーカスして全選択する（Ctrl+L・新しいタブ）。</summary>
    private void FocusBrowserAddress() {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        BrowserAddressBox.Focus();
        BrowserAddressBox.SelectAll();
    }
    private void OnBrowserAddressTextChanged(object sender, TextChangedEventArgs e)
        => BrowserAddressSuggestions.OnTextChanged();
    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e)
        => BrowserAddressSuggestions.HandleKey(e);
    private void OnBrowserAddressLostFocus(object sender, RoutedEventArgs e)
        => BrowserAddressSuggestions.OnLostFocus();

    /// <summary>ブックマーク／履歴／候補の行を開く。</summary>
    private async Task OpenBrowserLibraryUrlAsync(string url, bool newTab) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        if (newTab || _activeBrowserTab is null) {
            await CreateBrowserTabAsync(url);
            SaveActiveWorkspaceSnapshot();
            return;
        }
        await NavigateBrowserAsync(url);
    }

    // ── ツールバーの状態 ───────────────────────────────────────────────
    /// <summary>戻る/進むの活性・読み込み中・ズーム率を、<b>いま見ているタブ</b>から取り直す。</summary>
    private void UpdateBrowserToolbar(BrowserTab? tab) {
        if (tab is null || !ReferenceEquals(tab, _activeBrowserTab))
            return;
        BrowserDisplayMapper.ApplyToolbar(
            _vm.Browser, tab.View.TryCore(), tab.IsLoading, tab.View.ZoomFactor);
    }

    private void OnBrowserHome(object sender, RoutedEventArgs e) => NavigateBrowser(DefaultBrowserUrl);

    // ── ズーム ─────────────────────────────────────────────────────────
    /// <summary>表示倍率を段階的に変える（0 でリセット）。WebView2 の既定は Ctrl+ホイールのみなので、
    /// キーボードからも同じ段階で動かせるようにする。</summary>
    private void ZoomBrowser(int step) {
        if (ActiveBrowserView is not { } view)
            return;
        view.ZoomFactor = Math.Round(BrowserZoomPolicy.NextFactor(view.ZoomFactor, step), 2);
        UpdateBrowserToolbar(_activeBrowserTab);
    }
    private void OnBrowserZoomReset(object sender, RoutedEventArgs e) => ZoomBrowser(0);

    // ── ページ内検索（Ctrl+F） ─────────────────────────────────────────
    private void OnBrowserFindClose(object sender, RoutedEventArgs e) => BrowserFind.Close();
    private void OnBrowserFindKeyDown(object sender, KeyEventArgs e) => BrowserFind.HandleKey(e);

    // ── ダウンロード ───────────────────────────────────────────────────
    // ── キーボード ─────────────────────────────────────────────────────
    /// <summary>ブラウザペインにフォーカスがあるときのキー。ブラウザの慣習（F5・Alt+←→・Ctrl+L/F/D/T/W）を
    /// そのまま効かせる。<b>処理したら true</b> を返し、アプリ全体のキーバインドへは渡さない。</summary>
    private bool HandleBrowserKey(KeyEventArgs e) {
        if (!IsBrowserFocused())
            return false;
        switch (BrowserChromePolicy.ResolveKeyboardAction(
            e.Key, e.SystemKey, Keyboard.Modifiers, _vm.Browser.IsFindOpen)) {
            case BrowserKeyboardAction.Reload:
                OnBrowserReload(this, new RoutedEventArgs());
                break;
            case BrowserKeyboardAction.NavigateBack:
                BrowserNavigateHistory(back: true);
                break;
            case BrowserKeyboardAction.NavigateForward:
                BrowserNavigateHistory(back: false);
                break;
            case BrowserKeyboardAction.FocusAddress:
                FocusBrowserAddress();
                break;
            case BrowserKeyboardAction.OpenFind:
                BrowserFind.Open();
                break;
            case BrowserKeyboardAction.FindNext:
                BrowserFind.Step(1);
                break;
            case BrowserKeyboardAction.FindPrevious:
                BrowserFind.Step(-1);
                break;
            case BrowserKeyboardAction.ToggleBookmark:
                _vm.Browser.ToggleBookmark();
                break;
            case BrowserKeyboardAction.ToggleBookmarkBar:
                _vm.Browser.ToggleBookmarkBar();
                break;
            case BrowserKeyboardAction.ZoomIn:
                ZoomBrowser(1);
                break;
            case BrowserKeyboardAction.ZoomOut:
                ZoomBrowser(-1);
                break;
            case BrowserKeyboardAction.ZoomReset:
                ZoomBrowser(0);
                break;
            case BrowserKeyboardAction.CloseFind:
                BrowserFind.Close();
                break;
            case null:
                return false;
        }
        e.Handled = true;
        return true;
    }
    /// <summary>ブラウザペイン（アドレス欄・検索バー・WebView2 のどれか）にフォーカスがあるか。</summary>
    private bool IsBrowserFocused()
        => BrowserPane.IsKeyboardFocusWithin || _focusedRegion?.Pane == PaneKind.Browser;

    // ── 右クリック（素材の流れ） ───────────────────────────────────────
    /// <summary>ページの右クリックメニューに Loomo の項目を足す。選択テキストとリンクは頻度が高いので
    /// 先頭へ、ページ全体に効くものは「Loomo」サブメニューへ畳む（Chromium 既定の項目は残す）。</summary>
    private void OnBrowserContextMenuRequested(CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
        => BrowserContextMenuBuilder.AddItems(
            core, e, Dispatcher,
            selection => {
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
                _vm.AiBar.AskAbout(selection);
            },
            selection => {
                _vm.Pegboard.AddContent(selection, type: "text");
                ToastService.Success("選択テキストをペグボードへ残しました。");
            },
            (url, title) => {
                _vm.Pegboard.AddContent(url, type: "url", title: title);
                ToastService.Success("ページをペグボードへ残しました。");
            },
            link => {
                _ = CreateBrowserTabAsync(link);
                SaveActiveWorkspaceSnapshot();
            },
            OpenUrlInDetachedWindow,
            (link, linkText) => {
                _vm.Pegboard.AddContent(link, type: "url", title: linkText);
                ToastService.Success("リンクをペグボードへ残しました。");
            },
            () => _ = SendBrowserPageToEditorAsync(),
            _vm.Browser.IsBookmarked,
            _vm.Browser.BookmarkBarMenuText,
            _vm.Browser.ToggleBookmark,
            _vm.Browser.ToggleBookmarkBar,
            BrowserFind.Open,
            url => {
                try { Clipboard.SetText(url); } catch { /* クリップボード占有中は無視 */ }
            },
            url => {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { ToastService.Error("既定のブラウザを起動できませんでした。"); }
            });

    /// <summary>表示中のページを Markdown にしてエディタタブで開く（読む・引用する・貼り直すための素材化）。
    /// 拡張子を .md にしてあるので、そのまま EditorSupport のプレビューにも載る。</summary>
    private async Task SendBrowserPageToEditorAsync() {
        if (ActiveBrowserView.TryCore() is not { } core)
            return;
        var result = await BrowserPageMarkdown.SendToEditorAsync(
            core, _editor, () => EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor));
        if (result.ErrorMessage is { } error) {
            ToastService.Error($"ページを読み取れませんでした: {error}");
            return;
        }
        if (!result.HasContent) {
            ToastService.Info("本文として取り出せる内容がありませんでした。");
        }
    }
}
