namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ブラウザペイン（タブ管理・ナビゲーション・WebView2 遅延実体化）。
/// ツールバーの状態・ブックマーク・ページ内検索・ダウンロード・右クリックは
/// <see cref="ShellWindow"/> の BrowserChrome 側に分けてある。</summary>
public partial class ShellWindow {
    private void OnBrowserBack(object sender, RoutedEventArgs e) => BrowserNavigateHistory(back: true);
    private void OnBrowserForward(object sender, RoutedEventArgs e) => BrowserNavigateHistory(back: false);
    /// <summary>アクティブタブの履歴を1つ進退する。ツールバーのボタンと
    /// マウスの戻る/進むボタン（<see cref="OnShellPreviewMouseNavigate"/>）の共通の口。</summary>
    private void BrowserNavigateHistory(bool back) {
        if (ActiveBrowserView is not { } view)
            return;
        if (back) {
            if (view.CanGoBack)
                view.GoBack();
        } else if (view.CanGoForward) {
            view.GoForward();
        }
    }
    /// <summary>更新／停止（読み込み中は停止として働く。ボタンは1つで、絵と説明が入れ替わる）。</summary>
    private void OnBrowserReload(object sender, RoutedEventArgs e) {
        if (ActiveBrowserView?.CoreWebView2 is not { } core)
            return;
        if (_activeBrowserTab?.IsLoading == true)
            core.Stop();
        else
            core.Reload();
    }
    private async void OnBrowserNewTab(object sender, RoutedEventArgs e) {
        await CreateBrowserTabAsync(DefaultBrowserUrl);
        FocusBrowserAddress();
        SaveActiveWorkspaceSnapshot();
    }
    private void OnBrowserTabSelected(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateBrowserTab(id);
    }
    private async void OnBrowserTabClosed(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id }) {
            await CloseBrowserTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }
    private void OnBrowserNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) {
        if (sender is not WebView2CompositionControl view)
            return;
        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        if (tab is null)
            return;
        tab.IsLoading = false;
        UpdateBrowserTab(tab);
        UpdateBrowserToolbar(tab);
        _ = RefreshBrowserTabIconAsync(tab);
        if (ReferenceEquals(_activeBrowserTab, tab)) {
            var url = BrowserUrlOf(tab);
            // 入力中は横取りしない（NavigationStarting／SourceChanged と同じ理由。遅いページを開いた直後に
            // Ctrl+L で次の行き先を打ち始めると、読み込み完了で打ちかけの文字が消える）。
            if (!BrowserAddressBox.IsKeyboardFocusWithin)
                SetBrowserAddressText(url ?? string.Empty);
            if (e.IsSuccess) {
                RecordTrailBrowser(url, view.CoreWebView2?.DocumentTitle);
                _vm.Browser.RecordVisit(url, view.CoreWebView2?.DocumentTitle);
            }
            EvaluateBrowserExtensionPrompt(tab);
        }
    }
    private void OnBrowserNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs e) {
        tab.IsLoading = true;
        if (!ReferenceEquals(_activeBrowserTab, tab))
            return;
        _vm.Browser.IsLoading = true;
        // 遷移先を先に出す（読み込み待ちの間、どこへ向かっているか見えるように）。
        // 入力中は横取りしない——打っている最中に別の遷移が完了しても文字が消えないようにする。
        if (!BrowserAddressBox.IsKeyboardFocusWithin)
            SetBrowserAddressText(e.Uri);
    }
    private async void NavigateBrowser(string text) => await NavigateBrowserAsync(text);

    /// <summary>アドレスへ遷移する（実体化まで待つ待機可能版）。タブが無ければ 1 枚作る。</summary>
    private async Task NavigateBrowserAsync(string text) {
        var address = WorkspaceSessionCoordinator.NormalizeBrowserAddress(text, DefaultBrowserUrl);
        SetBrowserAddressText(address);   // 候補も閉じる
        var tab = _activeBrowserTab ?? await CreateBrowserTabAsync(address);
        tab.PendingUrl = address;
        await EnsureBrowserRealizedAsync(tab);
        if (tab.View.CoreWebView2 is { } core && tab.PendingUrl is not null) {
            tab.PendingUrl = null;
            if (!TryNavigateBrowserCore(core, address))
                return;
        }
        UpdateBrowserTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ブラウザペインを可視化・フォーカスして URL を開き、CoreWebView2 の実体化まで待つ
    /// （<see cref="Services.BrowserService.ShowAndNavigateRequested"/> のフック。フロントデバッグの CDP アタッチ前段）。</summary>
    private async Task ShowBrowserPaneAndNavigateAsync(string url) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        FocusPane(PaneKind.Browser);
        await NavigateBrowserAsync(url);
    }
    /// <summary>アドレスへ遷移する。<b>遷移の口はここ一箇所に絞って必ず例外を受け止める</b>——
    /// アドレス欄は何でも打てるので WebView2 が受け付けない文字列が来る。呼び出しは
    /// <c>async void</c> の先にあるため、投げっぱなしにするとアプリごと落ちる。</summary>
    private static bool TryNavigateBrowserCore(CoreWebView2 core, string address) {
        try {
            core.Navigate(address);
            return true;
        } catch (Exception ex) when (ex is ArgumentException or UriFormatException or COMException) {
            ToastService.Error($"このアドレスは開けません: {address}");
            return false;
        }
    }
    private WebView2CompositionControl? ActiveBrowserView => _activeBrowserTab?.View;
    /// <summary>そのタブが今いる URL。<b>WPF ラッパーの <c>Source</c> ではなく <c>CoreWebView2.Source</c> を
    /// 正本にする</b>——ラッパー側は <see cref="Uri"/> 型なので、<c>data:</c> のように Uri に載せ替えられない
    /// 遷移で前の値のまま取り残されることがある（アドレス欄に前のページの URL が居座る）。</summary>
    private static string? BrowserUrlOf(BrowserTab? tab) {
        if (tab is null)
            return null;
        // 空文字も「無い」として次の手掛かりへ落とす（Source は遷移の種類によって空で返ることがある）。
        return Empty(tab.View.CoreWebView2?.Source) ?? Empty(tab.View.Source?.ToString()) ?? Empty(tab.PendingUrl);
        static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
    private BrowserWorkspaceTabs CurrentBrowserWorkspace
        => _activeBrowserWorkspace ?? _scratchBrowserWorkspace;
    private async Task<BrowserTab> CreateBrowserTabAsync( string url, Guid? requestedId = null, string? requestedTitle = null) {
        var tab = CreateBrowserTab(url, requestedId, requestedTitle);
        await EnsureBrowserRealizedAsync(tab);
        return tab;
    }
    /// <param name="navigateSelf">false のとき <see cref="BrowserTab.PendingUrl"/> を立てない
    /// ＝自分ではナビゲートしない。<c>target="_blank"</c> の受け皿のように、遷移を WebView2 側が
    /// 行うタブで二重ナビゲートを避けるため。</param>
    private BrowserTab CreateBrowserTab( string url, Guid? requestedId = null, string? requestedTitle = null, bool navigateSelf = true) {
        var id = requestedId ?? Guid.NewGuid();
        var browserWorkspace = CurrentBrowserWorkspace;
        var view = new LoomoWebView2 {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E), Visibility = Visibility.Collapsed, CreationProperties = CreateWebViewCreationProperties()
        };
        view.NavigationCompleted += OnBrowserNavigationCompleted;
        var tab = new BrowserTab(id, view) {
            PendingUrl = navigateSelf ? WorkspaceSessionCoordinator.NormalizeBrowserAddress(url, DefaultBrowserUrl) : null
        };
        view.ZoomFactorChanged += (_, _) => UpdateBrowserToolbar(tab);
        _browserTabs.Add(tab);
        BrowserContentHost.Children.Add(view);
        _vm.Tabs.AddBrowserTab(id, requestedTitle ?? $"Tab {browserWorkspace.NextTabNumber++}", false);
        ActivateBrowserTab(id);
        return tab;
    }
    private async Task EnsureBrowserRealizedAsync(BrowserTab tab) {
        if (tab.RealizationStarted)
            return;
        tab.RealizationStarted = true;
        try {
            await tab.View.EnsureCoreWebView2Async();
        } catch {
            // 失敗の現実的な原因は「別の Loomo が同じプロファイルを違うブラウザ引数で握っている」
            // （0x8007139F、§21.5.3）。ポートを引き当て直して一度だけやり直し、駄目なら黙らずに知らせる。
            tab.RealizationStarted = false;   // 失敗時は次回の表示・操作で再試行できるようにする
            if (!WebViewEnvironment.TryRecover()) {
                WebViewEnvironment.ReportUnavailable("ブラウザ");
                return;
            }
            tab.View.CreationProperties = CreateWebViewCreationProperties();
            tab.RealizationStarted = true;
            try {
                await tab.View.EnsureCoreWebView2Async();
            } catch {
                tab.RealizationStarted = false;
                WebViewEnvironment.ReportUnavailable("ブラウザ");
                return;
            }
        }
        WebViewEnvironment.NoteCreated();
        ConfigureBrowserCore(tab, tab.View.CoreWebView2!);
        // 拡張機能のページ（設定画面）用の仕込みは<b>最初の遷移より先に</b>済ませる——ドキュメント生成時の
        // 仕込みなので、待たずに navigate すると開いたその画面だけ効かない（§21.5.2）。
        try {
            await tab.View.CoreWebView2!.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionPageBridge.Script);
        } catch {
            // 仕込めなくても普通のページには影響しない。
        }
        if (tab.PendingUrl is { } pending) {
            tab.PendingUrl = null;
            TryNavigateBrowserCore(tab.View.CoreWebView2!, pending);
        }
        UpdateBrowserTab(tab);
        UpdateBrowserToolbar(tab);
        await RefreshBrowserTabIconAsync(tab);
    }
    /// <summary>タブの CoreWebView2 に、このペインとしての振る舞いを結ぶ。
    /// <b>ここで結ばないと素の WebView2 の既定に落ちる</b>——とくに <c>NewWindowRequested</c> を
    /// 誰も扱わないと <c>target="_blank"</c> のリンクがツールバーの無い素っ気ない別窓で開く
    /// （<see cref="EditorSupportContextLink"/> に同じ罠の記録がある）。</summary>
    private void ConfigureBrowserCore(BrowserTab tab, CoreWebView2 core) {
        ConfigureBrowserCoreBasics(core);
        core.NewWindowRequested += (_, e) => OnBrowserNewWindowRequested(e);
        // window.close()。閉じると WebView2 を Dispose するので、通知の中から同期に呼ばない
        // （自分のイベントを配っている最中に足元を壊すことになる）。次のディスパッチへ回す。
        core.WindowCloseRequested += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => _ = CloseBrowserTabAsync(tab.Id)));
        core.NavigationStarting += (_, e) => OnBrowserNavigationStarting(tab, e);
        core.HistoryChanged += (_, _) => UpdateBrowserToolbar(tab);
        core.SourceChanged += (_, _) => {
            // 同一ページ内の遷移（History API）はナビゲーション完了が来ないので、ここで追う。
            if (ReferenceEquals(_activeBrowserTab, tab)) {
                if (!BrowserAddressBox.IsKeyboardFocusWithin)
                    SetBrowserAddressText(BrowserUrlOf(tab) ?? string.Empty);
                // ★の対象（CurrentUrl）も一緒に進める。アドレス欄だけ追うと、題を打ち替えない SPA では
                // DocumentTitleChanged も来ないので、★の表示と Ctrl+D が前のページのまま取り残される。
                // 履歴には触らない経路なので、訪問回数が増えることはない。
                _vm.Browser.SetCurrentPage(BrowserUrlOf(tab), core.DocumentTitle);
                // ストアは SPA なので、拡張機能ページへの移動はここでしか分からない（§21.5.2）。
                EvaluateBrowserExtensionPrompt(tab);
            }
            UpdateBrowserToolbar(tab);
        };
        core.DocumentTitleChanged += (_, _) => {
            UpdateBrowserTab(tab);
            // タイトルはナビゲーション完了より後に確定することが多い。履歴の見出しをここで揃える
            // （訪問として数え直さない——同じページを見ているだけ）。
            if (ReferenceEquals(_activeBrowserTab, tab)) {
                _vm.Browser.UpdateCurrentTitle(BrowserUrlOf(tab), core.DocumentTitle);
                // 促しバーの見出しは題から作る。題が確定するのは遷移完了より後。
                EvaluateBrowserExtensionPrompt(tab);
            }
        };
        core.WebMessageReceived += (_, e) => OnBrowserWebMessageReceived(tab, e);
        core.FaviconChanged += OnBrowserFaviconChanged;
        core.DownloadStarting += OnBrowserDownloadStarting;
        core.ContextMenuRequested += (_, e) => OnBrowserContextMenuRequested(core, e);
        HookBrowserFind(core);
    }
    /// <summary><c>target="_blank"</c>・<c>window.open</c> をこのペインの新しいタブで受ける。
    /// 生成した CoreWebView2 を <see cref="CoreWebView2NewWindowRequestedEventArgs.NewWindow"/> へ
    /// 渡すので、遷移は WebView2 が行う（opener との結びつきも保たれる）。</summary>
    private async void OnBrowserNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e) {
        var deferral = e.GetDeferral();
        var uri = e.Uri;
        BrowserTab? created = null;
        try {
            e.Handled = true;
            created = CreateBrowserTab(uri, navigateSelf: false);
            await EnsureBrowserRealizedAsync(created);
            if (created.View.CoreWebView2 is { } core) {
                e.NewWindow = core;
                return;
            }
            // 実体化できなかったときだけ、自分で開き直す（黙って何も起きないのが一番困る）。
            created.PendingUrl = uri;
            await EnsureBrowserRealizedAsync(created);
            if (created.View.CoreWebView2 is not null)
                return;
            // 2度目も駄目なら、この受け皿は使えない。Handled=true のまま NewWindow を渡さずに抜けると
            // リンクはどこにも開かず、活性化済みの空白タブだけが残る（＝一番困る結末そのもの）。
            // 例外時と同じ後始末をして既定動作へ戻す。
            e.Handled = false;
            await CloseBrowserTabAsync(created.Id);
        } catch {
            // 受け皿を用意できなければ既定動作に戻す。作りかけのタブは畳む——そうしないと
            // 空白タブが1枚残ったうえに、防ぎたかった素っ気ない別窓まで開く。
            e.Handled = false;
            if (created is not null)
                await CloseBrowserTabAsync(created.Id);
        } finally {
            deferral.Complete();
        }
    }
    /// <summary>タブでも切り離しウィンドウでも同じにしておきたい WebView2 の素の設定。</summary>
    private static void ConfigureBrowserCoreBasics(CoreWebView2 core) {
        var settings = core.Settings;
        settings.IsPasswordAutosaveEnabled = true;   // 既定 false：これが無いと保存プロンプトすら出ない
        settings.IsGeneralAutofillEnabled = true;    // 住所など一般フォームの自動入力
        core.PermissionRequested += OnBrowserPermissionRequested;
    }
    private static void OnBrowserPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) {
        e.SavesInProfile = true;
        if (e.PermissionKind == CoreWebView2PermissionKind.FileReadWrite)
            e.State = CoreWebView2PermissionState.Allow;
    }
    private void ScheduleBrowserRealize(BrowserTab? tab) {
        if (tab is null || tab.RealizationStarted || !(_stageActive || IsPaneVisible(PaneKind.Browser)))
            return;
        Dispatcher.BeginInvoke( DispatcherPriority.Background, new Action(() => {
                if (ReferenceEquals(_activeBrowserTab, tab) && (_stageActive || IsPaneVisible(PaneKind.Browser)))
                    _ = EnsureBrowserRealizedAsync(tab);
            }));
    }
    private async Task CloseBrowserTabAsync(Guid id) {
        var index = _browserTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;
        var wasActive = _activeBrowserTab?.Id == id;
        var tab = _browserTabs[index];
        if (tab.View.CoreWebView2 is not null)
            tab.View.CoreWebView2.FaviconChanged -= OnBrowserFaviconChanged;
        BrowserContentHost.Children.Remove(tab.View);
        tab.View.NavigationCompleted -= OnBrowserNavigationCompleted;
        tab.View.Dispose();
        _browserTabs.RemoveAt(index);
        _vm.Tabs.RemoveBrowserTab(id);
        if (!wasActive)
            return;
        if (_browserTabs.Count == 0) {
            await CreateBrowserTabAsync(DefaultBrowserUrl);
            return;
        }
        ActivateBrowserTab(_browserTabs[Math.Min(index, _browserTabs.Count - 1)].Id);
    }
    private void ActivateBrowserTab(Guid id) {
        var tab = _browserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;
        foreach (var browserTab in _browserTabs)
            browserTab.View.Visibility = browserTab.Id == id ? Visibility.Visible : Visibility.Collapsed;
        _activeBrowserTab = tab;
        CurrentBrowserWorkspace.ActiveTabId = id;
        _browser.SetActiveView(tab.View);
        _vm.Tabs.ActivateBrowserTab(id);
        var url = BrowserUrlOf(tab);
        SetBrowserAddressText(url ?? string.Empty);
        RecordTrailBrowser(url, tab.View.CoreWebView2?.DocumentTitle);
        // ★の状態・戻る/進むの活性・読み込み中は「今見ているタブ」のもの。切替のたびに揃える
        // （切り替えただけで訪問回数は増やさない）。
        _vm.Browser.SetCurrentPage(url, tab.View.CoreWebView2?.DocumentTitle);
        // 促しバーは「いま見ているタブ」のもの（裏のタブがストアでも出さない）。
        EvaluateBrowserExtensionPrompt(tab);
        UpdateBrowserToolbar(tab);
        tab.View.Focus();
        ScheduleBrowserRealize(tab);
        SaveActiveWorkspaceSnapshot();
    }
    private void UpdateBrowserTab(BrowserTab? tab) {
        if (tab is null)
            return;
        _vm.Tabs.UpdateBrowserTab(tab.Id, tab.View.CoreWebView2?.DocumentTitle);
        SaveActiveWorkspaceSnapshot();
    }
    private async void OnBrowserFaviconChanged(object? sender, object? e) {
        if (sender is not Microsoft.Web.WebView2.Core.CoreWebView2 coreWebView2)
            return;
        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View.CoreWebView2, coreWebView2));
        if (tab is null)
            return;
        await RefreshBrowserTabIconAsync(tab);
    }
    private async Task RefreshBrowserTabIconAsync(BrowserTab tab) {
        if (tab.View.CoreWebView2 is null)
            return;
        var icon = await _tabIcons.GetBrowserIconAsync(tab.View.CoreWebView2, BrowserUrlOf(tab));
        _vm.Tabs.UpdateTabIcon(tab.Id, icon);
    }
}
