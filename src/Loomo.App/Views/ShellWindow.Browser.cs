
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ブラウザペイン（タブ管理・ナビゲーション・WebView2 遅延実体化）</summary>
public partial class ShellWindow
{
    private void OnBrowserBack(object sender, RoutedEventArgs e)
    {
        var view = ActiveBrowserView;
        if (view?.CanGoBack == true)
            view.GoBack();
    }

    private void OnBrowserForward(object sender, RoutedEventArgs e)
    {
        var view = ActiveBrowserView;
        if (view?.CanGoForward == true)
            view.GoForward();
    }

    private void OnBrowserReload(object sender, RoutedEventArgs e)
        => ActiveBrowserView?.CoreWebView2?.Reload();

    private async void OnBrowserNewTab(object sender, RoutedEventArgs e)
    {
        await CreateBrowserTabAsync(DefaultBrowserUrl);
        SaveActiveWorkspaceSnapshot();
    }

    private void OnBrowserTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateBrowserTab(id);
    }

    private async void OnBrowserTabClosed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await CloseBrowserTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private void OnBrowserGo(object sender, RoutedEventArgs e)
        => NavigateBrowser(BrowserAddressBox.Text);

    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateBrowser(BrowserAddressBox.Text);
            e.Handled = true;
        }
    }

    private void OnBrowserNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (sender is not WebView2CompositionControl view)
            return;

        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        if (tab is null)
            return;

        UpdateBrowserTab(tab);
        _ = RefreshBrowserTabIconAsync(tab);
        if (ReferenceEquals(_activeBrowserTab, tab))
        {
            BrowserAddressBox.Text = view.Source?.ToString() ?? string.Empty;
            // 見えているタブの遷移だけを軌跡（操作ログ）へ記録する（背景タブの読込は対象外）。
            if (e.IsSuccess)
                RecordTrailBrowser(view.Source?.ToString(), view.CoreWebView2?.DocumentTitle);
        }
    }

    private async void NavigateBrowser(string text)
    {
        var address = WorkspaceSessionCoordinator.NormalizeBrowserAddress(text, DefaultBrowserUrl);
        BrowserAddressBox.Text = address;

        if (_activeBrowserTab is not { } tab)
            return;

        // 未実体化なら、この URL を保留先にして実体化（＝そのままナビゲート）する。
        tab.PendingUrl = address;
        await EnsureBrowserRealizedAsync(tab);

        // 既に実体化済みだった場合は PendingUrl が消費されないので、明示的にナビゲートする。
        if (tab.View.CoreWebView2 is { } core && tab.PendingUrl is not null)
        {
            tab.PendingUrl = null;
            core.Navigate(address);
        }
        UpdateBrowserTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private WebView2CompositionControl? ActiveBrowserView => _activeBrowserTab?.View;

    private BrowserWorkspaceTabs CurrentBrowserWorkspace
        => _activeBrowserWorkspace ?? _scratchBrowserWorkspace;

    // ブラウザタブを生成して即座に WebView2 まで実体化する（新規タブなど、直後に CoreWebView2 を 使う呼び出し向け）。起動経路は CreateBrowserTab（遅延）を使う。
    private async Task<BrowserTab> CreateBrowserTabAsync(
        string url,
        Guid? requestedId = null,
        string? requestedTitle = null)
    {
        var tab = CreateBrowserTab(url, requestedId, requestedTitle);
        await EnsureBrowserRealizedAsync(tab);
        return tab;
    }

    // ブラウザタブの器（WebView2 コントロール・タブUI）だけを同期で用意し、CoreWebView2 の生成は遅延する。 重い EnsureCoreWebView2Async を起動の臨界パスから外すのが狙い。実体化は Browser ペインが 見えてアクティブになった時（ScheduleBrowserRealize）に背景優先度で行う。
    private BrowserTab CreateBrowserTab(
        string url,
        Guid? requestedId = null,
        string? requestedTitle = null)
    {
        var id = requestedId ?? Guid.NewGuid();
        var browserWorkspace = CurrentBrowserWorkspace;
        var view = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            Visibility = Visibility.Collapsed,
            // 全タブで同じユーザーデータフォルダを共有 → Cookie・保存パスワード・サイト権限が
            // タブ間で共通になり、再ビルド・再起動をまたいで残る。
            CreationProperties = CreateWebViewCreationProperties()
        };
        view.NavigationCompleted += OnBrowserNavigationCompleted;

        var tab = new BrowserTab(id, view)
        {
            PendingUrl = WorkspaceSessionCoordinator.NormalizeBrowserAddress(url, DefaultBrowserUrl)
        };
        _browserTabs.Add(tab);
        BrowserContentHost.Children.Add(view);
        _vm.Tabs.AddBrowserTab(id, requestedTitle ?? $"Tab {browserWorkspace.NextTabNumber++}", false);
        ActivateBrowserTab(id);
        return tab;
    }

    // タブの CoreWebView2 を生成し、保留中の URL があればナビゲートする（冪等・多重生成防止）。
    private async Task EnsureBrowserRealizedAsync(BrowserTab tab)
    {
        if (tab.RealizationStarted)
            return;
        tab.RealizationStarted = true;
        try
        {
            await tab.View.EnsureCoreWebView2Async();
        }
        catch
        {
            tab.RealizationStarted = false;   // 失敗時は次回の表示・操作で再試行できるようにする
            return;
        }

        ConfigureBrowserCore(tab.View.CoreWebView2!);
        tab.View.CoreWebView2!.FaviconChanged += OnBrowserFaviconChanged;
        if (tab.PendingUrl is { } pending)
        {
            tab.PendingUrl = null;
            tab.View.Source = new Uri(pending);
        }
        UpdateBrowserTab(tab);
        await RefreshBrowserTabIconAsync(tab);
    }

    // 実体化した CoreWebView2 を通常ブラウザらしく設定する：パスワードの自動保存・自動入力を有効化し、 サイト権限（フォルダ/ファイルアクセス・通知・位置情報など）の許可/拒否をプロファイルへ保存させる。 永続化先は WebViewUserDataFolder。
    private static void ConfigureBrowserCore(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsPasswordAutosaveEnabled = true;   // 既定 false：これが無いと保存プロンプトすら出ない
        settings.IsGeneralAutofillEnabled = true;    // 住所など一般フォームの自動入力

        core.PermissionRequested += OnBrowserPermissionRequested;
    }

    // サイト権限リクエストの扱い。原則は既定UI（許可/拒否ダイアログ）に任せつつ、ユーザーの選択を プロファイルへ保存して次回以降は再確認しないようにする（CoreWebView2PermissionRequestedEventArgs.SavesInProfile）。 ただし File System Access API（フォルダ/ファイルの読み書き許可）は Chromium が原則セッション 限りでしか権限を保持しないため、SavesInProfile を立てても起動のたびに再確認される。 dev ツール用途として、この権限だけは自動的に許可してプロンプトを抑止する。
    private static void OnBrowserPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.SavesInProfile = true;

        if (e.PermissionKind == CoreWebView2PermissionKind.FileReadWrite)
            e.State = CoreWebView2PermissionState.Allow;
    }

    // Browser ペインが表示中なら、アクティブなブラウザタブの WebView2 実体化を背景優先度で予約する。 起動・レイアウト変更の臨界パスをブロックしないよう DispatcherPriority.Background で遅延実行する。
    private void ScheduleBrowserRealize(BrowserTab? tab)
    {
        // ステージモード中はブラウザも必ず（舞台か袖の）どこかに見えている。
        if (tab is null || tab.RealizationStarted || !(_stageActive || IsPaneVisible(PaneKind.Browser)))
            return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                // 予約後に別タブへ切替・ペイン非表示になっていたら実体化しない。
                if (ReferenceEquals(_activeBrowserTab, tab) && (_stageActive || IsPaneVisible(PaneKind.Browser)))
                    _ = EnsureBrowserRealizedAsync(tab);
            }));
    }

    private async Task CloseBrowserTabAsync(Guid id)
    {
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

        if (_browserTabs.Count == 0)
        {
            await CreateBrowserTabAsync(DefaultBrowserUrl);
            return;
        }

        ActivateBrowserTab(_browserTabs[Math.Min(index, _browserTabs.Count - 1)].Id);
    }

    private void ActivateBrowserTab(Guid id)
    {
        var tab = _browserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;

        foreach (var browserTab in _browserTabs)
            browserTab.View.Visibility = browserTab.Id == id ? Visibility.Visible : Visibility.Collapsed;

        _activeBrowserTab = tab;
        CurrentBrowserWorkspace.ActiveTabId = id;
        // AIのブラウザ操作（IBrowserService）の対象を、いま見えているタブへ一本化する。
        _browser.SetActiveView(tab.View);
        _vm.Tabs.ActivateBrowserTab(id);
        // 未実体化のタブは Source が null なので、保留中の遷移先 URL を表示する。
        BrowserAddressBox.Text = tab.View.Source?.ToString() ?? tab.PendingUrl ?? string.Empty;
        // 読込済みタブへの切替では NavigationCompleted が発生しないため、活性化時にも現在地を記録する。
        RecordTrailBrowser(
            tab.View.Source?.ToString() ?? tab.PendingUrl,
            tab.View.CoreWebView2?.DocumentTitle);
        tab.View.Focus();
        // Browser ペインが見えていれば、このタブの WebView2 を背景で実体化する。
        ScheduleBrowserRealize(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private void UpdateBrowserTab(BrowserTab? tab)
    {
        if (tab is null)
            return;

        _vm.Tabs.UpdateBrowserTab(tab.Id, tab.View.CoreWebView2?.DocumentTitle);
        SaveActiveWorkspaceSnapshot();
    }

    private async void OnBrowserFaviconChanged(object? sender, object? e)
    {
        if (sender is not Microsoft.Web.WebView2.Core.CoreWebView2 coreWebView2)
            return;

        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View.CoreWebView2, coreWebView2));
        if (tab is null)
            return;

        await RefreshBrowserTabIconAsync(tab);
    }

    private async Task RefreshBrowserTabIconAsync(BrowserTab tab)
    {
        if (tab.View.CoreWebView2 is null)
            return;

        var icon = await _tabIcons.GetBrowserIconAsync(tab.View.CoreWebView2, tab.View.Source?.ToString());
        _vm.Tabs.UpdateTabIcon(tab.Id, icon);
    }
}
