namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ブラウザペインの「道具としての手触り」——ツールバーの状態、アドレス欄の候補、
/// ブックマークと履歴、ページ内検索、ズーム、ダウンロード、キーボード、そして右クリックからの
/// <b>素材の流れ</b>（設計書 §23.3 の共通語彙「〜へ送る」）。
///
/// <para>タブの生成・切替・遷移そのものは <c>ShellWindow.Browser.cs</c> 側にある。</para></summary>
public partial class ShellWindow {
    /// <summary>アドレス欄へプログラムから書いた変更で候補ドロップダウンを開かないための門。</summary>
    private bool _suppressBrowserAddressSuggest;

    /// <summary>この WebView2 ランタイムに検索 API（<c>CoreWebView2.Find</c>）が無い。
    /// 一度分かったら二度と試さず、バーには理由を出す（黙って何も起きないのを避ける）。</summary>
    private bool _browserFindUnavailable;

    private void InitializeBrowserChrome() {
        var vm = _vm.Browser;
        vm.OpenUrlRequested += (_, request) => _ = OpenBrowserLibraryUrlAsync(request.Url, request.NewTab);
        vm.OpenFileInEditorRequested += (_, path) => _ = OpenFileInNewEditorTabAsync(path);
        vm.FindChanged += (_, _) => _ = ApplyBrowserFindAsync();
        vm.FindStepRequested += (_, step) => StepBrowserFind(step);
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
    /// <summary>アドレス欄の文字を差し替える（候補は開かない）。表示中 URL の反映はすべてここを通す。</summary>
    private void SetBrowserAddressText(string text) {
        _suppressBrowserAddressSuggest = true;
        try {
            BrowserAddressBox.Text = text;
        } finally {
            _suppressBrowserAddressSuggest = false;
        }
        CloseBrowserSuggestions();
    }
    /// <summary>候補を閉じる。<b>選択も必ず一緒に落とす</b>——閉じても選択が残っていると、
    /// あとから Enter を押したときに、いま打っている文字ではなく前に選んだ候補へ飛ぶ。</summary>
    private void CloseBrowserSuggestions() {
        _vm.Browser.IsSuggestionsOpen = false;
        BrowserSuggestList.SelectedIndex = -1;
    }
    /// <summary>アドレス欄へフォーカスして全選択する（Ctrl+L・新しいタブ）。</summary>
    private void FocusBrowserAddress() {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        BrowserAddressBox.Focus();
        BrowserAddressBox.SelectAll();
    }
    private void OnBrowserAddressTextChanged(object sender, TextChangedEventArgs e) {
        if (_suppressBrowserAddressSuggest)
            return;
        _vm.Browser.UpdateSuggestions(BrowserAddressBox.Text);
    }
    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e) {
        var vm = _vm.Browser;
        switch (e.Key) {
            case Key.Enter:
                var chosen = vm.IsSuggestionsOpen
                    ? BrowserSuggestList.SelectedItem as BrowserLinkViewModel
                    : null;
                CloseBrowserSuggestions();
                NavigateBrowser(chosen?.Url ?? BrowserAddressBox.Text);
                e.Handled = true;
                break;
            case Key.Down when vm.IsSuggestionsOpen:
                MoveBrowserSuggestion(1);
                e.Handled = true;
                break;
            case Key.Up when vm.IsSuggestionsOpen:
                MoveBrowserSuggestion(-1);
                e.Handled = true;
                break;
            case Key.Escape:
                if (vm.IsSuggestionsOpen)
                    CloseBrowserSuggestions();
                else
                    SetBrowserAddressText(BrowserUrlOf(_activeBrowserTab) ?? string.Empty);
                e.Handled = true;
                break;
        }
    }
    /// <summary>候補の選択を上下に動かす（端で止める——回り込むと今どこにいるか分からなくなる）。</summary>
    private void MoveBrowserSuggestion(int delta) {
        var count = BrowserSuggestList.Items.Count;
        if (count == 0)
            return;
        var next = Math.Clamp(BrowserSuggestList.SelectedIndex + delta, 0, count - 1);
        BrowserSuggestList.SelectedIndex = next;
        BrowserSuggestList.ScrollIntoView(BrowserSuggestList.SelectedItem);
    }
    private void OnBrowserAddressLostFocus(object sender, RoutedEventArgs e) {
        // 候補の項目をクリックしている最中は閉じない（クリックが届く前に消えてしまう）。
        if (!BrowserSuggestList.IsKeyboardFocusWithin && !BrowserSuggestList.IsMouseOver)
            CloseBrowserSuggestions();
    }

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
        var vm = _vm.Browser;
        var core = tab.View.TryCore();
        vm.CanGoBack = core is not null && core.CanGoBack;
        vm.CanGoForward = core is not null && core.CanGoForward;
        vm.IsLoading = tab.IsLoading;
        vm.ZoomPercent = (int)Math.Round(tab.View.ZoomFactor * 100);
    }

    private void OnBrowserHome(object sender, RoutedEventArgs e) => NavigateBrowser(DefaultBrowserUrl);

    // ── ズーム ─────────────────────────────────────────────────────────
    /// <summary>表示倍率を段階的に変える（0 でリセット）。WebView2 の既定は Ctrl+ホイールのみなので、
    /// キーボードからも同じ段階で動かせるようにする。</summary>
    private void ZoomBrowser(int step) {
        if (ActiveBrowserView is not { } view)
            return;
        var factor = step == 0 ? 1.0 : Math.Clamp(view.ZoomFactor + step * 0.1, 0.25, 4.0);
        view.ZoomFactor = Math.Round(factor, 2);
        UpdateBrowserToolbar(_activeBrowserTab);
    }
    private void OnBrowserZoomReset(object sender, RoutedEventArgs e) => ZoomBrowser(0);

    // ── ページ内検索（Ctrl+F） ─────────────────────────────────────────
    private void HookBrowserFind(CoreWebView2 core) {
        if (_browserFindUnavailable)
            return;
        try {
            var find = core.Find;
            find.MatchCountChanged += (_, _) => Dispatcher.BeginInvoke(() => UpdateBrowserFindLabel(core));
            find.ActiveMatchIndexChanged += (_, _) => Dispatcher.BeginInvoke(() => UpdateBrowserFindLabel(core));
        } catch {
            // 古い WebView2 ランタイムには Find API が無い。検索バーは出すが、理由を出して黙らせない。
            _browserFindUnavailable = true;
        }
    }
    private void OpenBrowserFind() {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        _vm.Browser.IsFindOpen = true;
        // バーは可視になった直後にしかフォーカスできない（レイアウト確定待ち）。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
            BrowserFindBox.Focus();
            BrowserFindBox.SelectAll();
        }));
        if (!string.IsNullOrEmpty(_vm.Browser.FindTerm))
            _ = ApplyBrowserFindAsync();
    }
    private void CloseBrowserFind() {
        if (!_vm.Browser.IsFindOpen)
            return;
        try { ActiveBrowserView.TryCore()?.Find.Stop(); } catch { /* 未対応ランタイム */ }
        _vm.Browser.CloseFind();
        ActiveBrowserView?.Focus();
    }
    private void OnBrowserFindClose(object sender, RoutedEventArgs e) => CloseBrowserFind();
    private void OnBrowserFindKeyDown(object sender, KeyEventArgs e) {
        switch (e.Key) {
            case Key.Enter:
                StepBrowserFind(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseBrowserFind();
                e.Handled = true;
                break;
        }
    }
    private async Task ApplyBrowserFindAsync() {
        if (ActiveBrowserView.TryCore() is not { } core)
            return;
        var vm = _vm.Browser;
        if (_browserFindUnavailable) {
            vm.FindLabel = "この WebView2 では未対応";
            return;
        }
        try {
            if (string.IsNullOrEmpty(vm.FindTerm)) {
                core.Find.Stop();
                vm.FindLabel = "";
                return;
            }
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = vm.FindTerm;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;   // 自前のバーを出すので既定のバーは出さない
            await core.Find.StartAsync(options);
            UpdateBrowserFindLabel(core);
        } catch {
            // ここでの失敗は「API が無い」ではなく、遷移中に呼んだ等の一時的な事情のことがある。
            // 未対応の烙印（<see cref="_browserFindUnavailable"/>）は能力の確認をする
            // <see cref="HookBrowserFind"/> だけが押す——ここで押すと、一度の失敗で
            // セッション中ずっと Ctrl+F が死んだうえに嘘の理由が出る。
            vm.FindLabel = "検索できませんでした";
        }
    }
    private void StepBrowserFind(int step) {
        if (ActiveBrowserView.TryCore() is not { } core || _browserFindUnavailable)
            return;
        try {
            if (step >= 0)
                core.Find.FindNext();
            else
                core.Find.FindPrevious();
            UpdateBrowserFindLabel(core);
        } catch {
            // 次/前の失敗も一時的なものとして扱う（理由は ApplyBrowserFindAsync のコメント）。
        }
    }
    private void UpdateBrowserFindLabel(CoreWebView2 core) {
        try { _vm.Browser.SetFindMatches(core.Find.ActiveMatchIndex, core.Find.MatchCount); }
        catch { /* セッション終了直後などは読めない */ }
    }

    // ── ダウンロード ───────────────────────────────────────────────────
    /// <summary>既定のダウンロード UI（WebView2 が出す小窓）を止めて、ペインの一覧で見せる。
    /// 完了後にエディタで開く／フォルダーを出すといった<b>次の一手</b>へ繋げるため。</summary>
    private void OnBrowserDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) {
        var operation = e.DownloadOperation;
        e.Handled = true;
        var item = new BrowserDownloadViewModel {
            Url = operation.Uri,
            FileName = Path.GetFileName(e.ResultFilePath),
            FilePath = e.ResultFilePath,
            StatusText = "受信中…",
            Operation = operation,
        };
        var vm = _vm.Browser;
        vm.Downloads.Insert(0, item);
        vm.IsDownloadsOpen = true;
        vm.NotifyDownloadsChanged();
        void Refresh() => Dispatcher.BeginInvoke(new Action(() => UpdateBrowserDownload(item)));
        operation.BytesReceivedChanged += (_, _) => Refresh();
        operation.StateChanged += (_, _) => Refresh();
        UpdateBrowserDownload(item);
    }
    private void UpdateBrowserDownload(BrowserDownloadViewModel item) {
        if (item.Operation is not { } operation)
            return;
        var received = (long?)operation.BytesReceived ?? 0;
        var total = (long?)operation.TotalBytesToReceive ?? 0;
        item.IsIndeterminate = total <= 0;
        item.Progress = total > 0 ? Math.Clamp(received * 100.0 / total, 0, 100) : 0;
        switch (operation.State) {
            case CoreWebView2DownloadState.Completed:
                item.IsActive = false;
                item.IsCompleted = true;
                item.FilePath = operation.ResultFilePath;
                item.FileName = Path.GetFileName(operation.ResultFilePath);
                item.Progress = 100;
                item.StatusText = $"完了 · {FormatBytes(received)}";
                break;
            case CoreWebView2DownloadState.Interrupted:
                item.IsActive = false;
                item.IsCompleted = false;
                item.StatusText = "中断";
                break;
            default:
                item.StatusText = total > 0
                    ? $"{FormatBytes(received)} / {FormatBytes(total)}"
                    : FormatBytes(received);
                break;
        }
        _vm.Browser.NotifyDownloadsChanged();
    }
    private static string FormatBytes(long bytes) => bytes switch {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    // ── キーボード ─────────────────────────────────────────────────────
    /// <summary>ブラウザペインにフォーカスがあるときのキー。ブラウザの慣習（F5・Alt+←→・Ctrl+L/F/D/T/W）を
    /// そのまま効かせる。<b>処理したら true</b> を返し、アプリ全体のキーバインドへは渡さない。</summary>
    private bool HandleBrowserKey(KeyEventArgs e) {
        if (!IsBrowserFocused())
            return false;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Alt+←→ は e.Key ではなく SystemKey に入る。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Shift 付きは<b>ここで取らない</b>。Ctrl+Shift+F はアプリ全体の「検索を開く」（`pane.search`）で、
        // Shift を見ないとブラウザにフォーカスがある間だけ検索ペインが開かなくなる。ズームだけは
        // 例外で Shift を許す（配列によっては `+` が Shift+キーで、衝突する割り当ても無い）。
        switch (key) {
            case Key.F5:
            case Key.R when ctrl && !shift:
                OnBrowserReload(this, new RoutedEventArgs());
                break;
            case Key.Left when alt:
                BrowserNavigateHistory(back: true);
                break;
            case Key.Right when alt:
                BrowserNavigateHistory(back: false);
                break;
            case Key.L when ctrl && !shift:
                FocusBrowserAddress();
                break;
            case Key.F when ctrl && !shift:
                OpenBrowserFind();
                break;
            case Key.G when ctrl:
                StepBrowserFind(shift ? -1 : 1);
                break;
            case Key.D when ctrl && !shift:
                _vm.Browser.ToggleBookmark();
                break;
            // ブックマークバーの表示切替。Shift 付きをここで取る数少ない例外だが、
            // Ctrl+Shift+B に他の割り当ては無く、ブラウザの慣習どおりで迷いようがない。
            case Key.B when ctrl && shift:
                _vm.Browser.ToggleBookmarkBar();
                break;
            // Ctrl+T（新しいタブ）と Ctrl+W（タブを閉じる）はブラウザの慣習だが、ここでは<b>取らない</b>。
            // Loomo では Ctrl+T が舞台／レイアウトの巡回、Ctrl+W がペイン操作のプレフィックス（vim 風）で、
            // 部屋そのものの動線だから——ブラウザにフォーカスがある間だけ効かなくなる方が混乱する。
            // 新しいタブは ＋ ボタンと `tab.newBrowser`（設定でキー割当可）、閉じるはタブの × と中クリック。
            case Key.OemPlus when ctrl:
            case Key.Add when ctrl:
                ZoomBrowser(1);
                break;
            case Key.OemMinus when ctrl:
            case Key.Subtract when ctrl:
                ZoomBrowser(-1);
                break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                ZoomBrowser(0);
                break;
            case Key.Escape when _vm.Browser.IsFindOpen:
                CloseBrowserFind();
                break;
            default:
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
    private void OnBrowserContextMenuRequested(CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e) {
        try {
            var target = e.ContextMenuTarget;
            var items = new List<CoreWebView2ContextMenuItem>();
            if (target.HasSelection && !string.IsNullOrWhiteSpace(target.SelectionText)) {
                var selection = target.SelectionText;
                items.Add(BrowserMenuItem(core, "AIへ送る", () => {
                    EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
                    _vm.AiBar.AskAbout(selection);
                }));
                items.Add(BrowserMenuItem(core, "ペグボードへ送る", () => {
                    _vm.Pegboard.AddContent(selection, type: "text");
                    ToastService.Success("選択テキストをペグボードへ残しました。");
                }));
            }
            if (target.HasLinkUri && !string.IsNullOrWhiteSpace(target.LinkUri)) {
                var link = target.LinkUri;
                var linkText = string.IsNullOrWhiteSpace(target.LinkText) ? null : target.LinkText;
                items.Add(BrowserMenuItem(core, "リンクを新しいタブで開く", () => {
                    _ = CreateBrowserTabAsync(link);
                    SaveActiveWorkspaceSnapshot();
                }));
                items.Add(BrowserMenuItem(core, "リンクを別ウィンドウで開く", () => OpenUrlInDetachedWindow(link)));
                items.Add(BrowserMenuItem(core, "リンクをペグボードへピン", () => {
                    _vm.Pegboard.AddContent(link, type: "url", title: linkText);
                    ToastService.Success("リンクをペグボードへ残しました。");
                }));
            }
            if (items.Count > 0)
                items.Add(BrowserSeparator(core));
            items.Add(BuildBrowserPageMenu(core));
            for (var i = 0; i < items.Count; i++)
                e.MenuItems.Insert(i, items[i]);
        } catch {
            // メニューを組めなくても既定のメニューは出す。
        }
    }
    /// <summary>ページ全体に効く操作（「Loomo」サブメニュー）。</summary>
    private CoreWebView2ContextMenuItem BuildBrowserPageMenu(CoreWebView2 core) {
        var parent = core.Environment.CreateContextMenuItem("Loomo", null, CoreWebView2ContextMenuItemKind.Submenu);
        var url = core.Source;
        var title = core.DocumentTitle;
        parent.Children.Add(BrowserMenuItem(core, "このページをエディタへ送る（Markdown）",
            () => _ = SendBrowserPageToEditorAsync()));
        parent.Children.Add(BrowserMenuItem(core, "このページをペグボードへピン", () => {
            _vm.Pegboard.AddContent(url, type: "url", title: title);
            ToastService.Success("ページをペグボードへ残しました。");
        }));
        parent.Children.Add(BrowserMenuItem(core,
            _vm.Browser.IsBookmarked ? "ブックマークを外す" : "ブックマークに追加",
            () => _vm.Browser.ToggleBookmark()));
        parent.Children.Add(BrowserMenuItem(core, _vm.Browser.BookmarkBarMenuText,
            () => _vm.Browser.ToggleBookmarkBar()));
        parent.Children.Add(BrowserSeparator(core));
        parent.Children.Add(BrowserMenuItem(core, "ページ内を検索…", OpenBrowserFind));
        parent.Children.Add(BrowserMenuItem(core, "URL をコピー", () => {
            try { Clipboard.SetText(url); } catch { /* クリップボード占有中は無視 */ }
        }));
        parent.Children.Add(BrowserMenuItem(core, "外部ブラウザで開く", () => {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { ToastService.Error("既定のブラウザを起動できませんでした。"); }
        }));
        return parent;
    }
    /// <summary>選択時の処理は UI スレッドへ渡す（メニューの通知はブラウザ側のスレッドで来る）。</summary>
    private CoreWebView2ContextMenuItem BrowserMenuItem(CoreWebView2 core, string label, Action action) {
        var item = core.Environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(action);
        return item;
    }
    private static CoreWebView2ContextMenuItem BrowserSeparator(CoreWebView2 core)
        => core.Environment.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator);

    /// <summary>表示中のページを Markdown にしてエディタタブで開く（読む・引用する・貼り直すための素材化）。
    /// 拡張子を .md にしてあるので、そのまま EditorSupport のプレビューにも載る。</summary>
    private async Task SendBrowserPageToEditorAsync() {
        if (ActiveBrowserView.TryCore() is not { } core)
            return;
        string body;
        try {
            var json = await core.ExecuteScriptAsync(BrowserPageMarkdown.ExtractScript);
            body = JsonSerializer.Deserialize<string>(json) ?? "";
        } catch (Exception ex) {
            ToastService.Error($"ページを読み取れませんでした: {ex.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(body)) {
            ToastService.Info("本文として取り出せる内容がありませんでした。");
            return;
        }
        var url = core.Source;
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            FileName = BrowserPageMarkdown.FileNameFor(url),
            Content = BrowserPageMarkdown.BuildDocument(core.DocumentTitle, url, body),
            OnSaved = _ => { },   // 閲覧・編集用：保存してもページ側へは戻らない
        });
    }
}
