
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ブラウザペイン（タブ管理・ナビゲーション・WebView2 遅延実体化）</summary>
public partial class ShellWindow {
    private void OnBrowserBack(object sender, RoutedEventArgs e) {
        var view = ActiveBrowserView;
        if (view?.CanGoBack == true)
            view.GoBack();
    }

    private void OnBrowserForward(object sender, RoutedEventArgs e) {
        var view = ActiveBrowserView;
        if (view?.CanGoForward == true)
            view.GoForward();
    }

    private void OnBrowserReload(object sender, RoutedEventArgs e)
        => ActiveBrowserView?.CoreWebView2?.Reload();

    private async void OnBrowserNewTab(object sender, RoutedEventArgs e) {
        await CreateBrowserTabAsync(DefaultBrowserUrl);
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

    private void OnBrowserGo(object sender, RoutedEventArgs e)
        => NavigateBrowser(BrowserAddressBox.Text);

    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            NavigateBrowser(BrowserAddressBox.Text);
            e.Handled = true;
        }
    }

    private void OnBrowserNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) {
        if (sender is not WebView2CompositionControl view)
            return;

        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        if (tab is null)
            return;

        UpdateBrowserTab(tab);
        _ = RefreshBrowserTabIconAsync(tab);
        if (ReferenceEquals(_activeBrowserTab, tab)) {
            BrowserAddressBox.Text = view.Source?.ToString() ?? string.Empty;
            if (e.IsSuccess)
                RecordTrailBrowser(view.Source?.ToString(), view.CoreWebView2?.DocumentTitle);
        }
    }

    private async void NavigateBrowser(string text) {
        var address = WorkspaceSessionCoordinator.NormalizeBrowserAddress(text, DefaultBrowserUrl);
        BrowserAddressBox.Text = address;

        if (_activeBrowserTab is not { } tab)
            return;

        tab.PendingUrl = address;
        await EnsureBrowserRealizedAsync(tab);

        if (tab.View.CoreWebView2 is { } core && tab.PendingUrl is not null) {
            tab.PendingUrl = null;
            core.Navigate(address);
        }
        UpdateBrowserTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private WebView2CompositionControl? ActiveBrowserView => _activeBrowserTab?.View;

    private BrowserWorkspaceTabs CurrentBrowserWorkspace
        => _activeBrowserWorkspace ?? _scratchBrowserWorkspace;

    private async Task<BrowserTab> CreateBrowserTabAsync(
        string url,
        Guid? requestedId = null,
        string? requestedTitle = null) {
        var tab = CreateBrowserTab(url, requestedId, requestedTitle);
        await EnsureBrowserRealizedAsync(tab);
        return tab;
    }

    private BrowserTab CreateBrowserTab(
        string url,
        Guid? requestedId = null,
        string? requestedTitle = null) {
        var id = requestedId ?? Guid.NewGuid();
        var browserWorkspace = CurrentBrowserWorkspace;
        var view = new WebView2CompositionControl {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            Visibility = Visibility.Collapsed,
            CreationProperties = CreateWebViewCreationProperties()
        };
        view.NavigationCompleted += OnBrowserNavigationCompleted;

        var tab = new BrowserTab(id, view) {
            PendingUrl = WorkspaceSessionCoordinator.NormalizeBrowserAddress(url, DefaultBrowserUrl)
        };
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
            tab.RealizationStarted = false;   // 失敗時は次回の表示・操作で再試行できるようにする
            return;
        }

        ConfigureBrowserCore(tab.View.CoreWebView2!);
        tab.View.CoreWebView2!.FaviconChanged += OnBrowserFaviconChanged;
        if (tab.PendingUrl is { } pending) {
            tab.PendingUrl = null;
            tab.View.Source = new Uri(pending);
        }
        UpdateBrowserTab(tab);
        await RefreshBrowserTabIconAsync(tab);
    }

    private static void ConfigureBrowserCore(CoreWebView2 core) {
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

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => {
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
        BrowserAddressBox.Text = tab.View.Source?.ToString() ?? tab.PendingUrl ?? string.Empty;
        RecordTrailBrowser(
            tab.View.Source?.ToString() ?? tab.PendingUrl,
            tab.View.CoreWebView2?.DocumentTitle);
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

        var icon = await _tabIcons.GetBrowserIconAsync(tab.View.CoreWebView2, tab.View.Source?.ToString());
        _vm.Tabs.UpdateTabIcon(tab.Id, icon);
    }
}
