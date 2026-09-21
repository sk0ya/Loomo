using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザー拡張機能のポップアップ表示と、そのページからのタブ遷移を管理する。</summary>
internal sealed class BrowserExtensionPopupController
{
    private readonly Popup _popup;
    private readonly Border _host;
    private readonly Func<CoreWebView2CreationProperties> _createProperties;
    private readonly Action<string> _openPageInBrowserTab;
    private readonly Action<string> _setStatus;
    private LoomoWebView2? _view;
    private Task? _bridge;
    private bool _resizing;

    public BrowserExtensionPopupController(
        Popup popup,
        Border host,
        Func<CoreWebView2CreationProperties> createProperties,
        Action<string> openPageInBrowserTab,
        Action<string> setStatus)
    {
        _popup = popup;
        _host = host;
        _createProperties = createProperties;
        _openPageInBrowserTab = openPageInBrowserTab;
        _setStatus = setStatus;
        _popup.Closed += OnClosed;
    }

    public async Task OpenAsync(string url)
    {
        var view = _view ??= CreateView();
        _host.Child = view;
        _popup.IsOpen = true;
        if (view.TryCore() is null)
        {
            try
            {
                await view.EnsureCoreWebView2Async();
                WebViewEnvironment.NoteCreated();
            }
            catch
            {
                _setStatus("この拡張機能の画面を開けませんでした。");
                return;
            }
        }

        if (view.TryCore() is not { } core)
            return;
        await (_bridge ??= ConfigureCoreAsync(core));
        BrowserNavigationCommand.TryNavigate(core, url);
    }

    private LoomoWebView2 CreateView()
    {
        var view = new LoomoWebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            CreationProperties = _createProperties(),
        };
        view.NavigationCompleted += async (_, _) => await FitToContentAsync(view);
        return view;
    }

    private async Task ConfigureCoreAsync(CoreWebView2 core)
    {
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenPageInBrowserTab(e.Uri);
        };
        core.WebMessageReceived += (_, e) =>
        {
            if (ExtensionPageBridgeService.TryReadOpenRequest(e.WebMessageAsJson, e.Source, out var target))
                OpenPageInBrowserTab(target);
        };
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionPageBridgeService.Script);
        }
        catch
        {
            // 仕込みに失敗してもポップアップは表示する。
        }
    }

    private void OpenPageInBrowserTab(string url)
    {
        _popup.IsOpen = false;
        _openPageInBrowserTab(url);
    }

    private async Task FitToContentAsync(LoomoWebView2 view)
    {
        if (view.TryCore() is not { } core
            || !_popup.IsOpen
            || core.Source is null or "" or "about:blank")
            return;

        try
        {
            var json = await core.ExecuteScriptAsync(BrowserExtensionScripts.PopupMeasure);
            if (!BrowserExtensionPopupLayout.TryParseSize(json, out var size))
                return;
            _host.Width = size.Width;
            _host.Height = size.Height;
            ResizeWindow();
        }
        catch
        {
            // 測れない場合は既定の大きさで表示する。
        }
    }

    private void ResizeWindow()
    {
        if (!_popup.IsOpen)
            return;
        _resizing = true;
        _popup.IsOpen = false;
        _popup.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _popup.IsOpen = true;
            _resizing = false;
        }));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_resizing && _view?.TryCore() is { } core)
            BrowserNavigationCommand.TryNavigate(core, "about:blank");
    }
}
