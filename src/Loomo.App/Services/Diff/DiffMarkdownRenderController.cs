using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>DiffSessionView の Markdown 差分 WebView とページ遷移を管理する。</summary>
internal sealed class DiffMarkdownRenderController : IDisposable
{
    private readonly Panel _host;
    private readonly Dispatcher _dispatcher;
    private IEditorSupportViewFactory? _viewFactory;
    private EditorSupportNavigationService? _navigation;
    private DiffSessionViewModel? _viewModel;
    private WebView2CompositionControl? _web;
    private Task<bool>? _init;
    private Window? _webWindow;
    private Window? _attachedWindow;
    private int _renderSeq;
    private bool _pageReady;
    private int _pendingChange = -1;
    private string? _navigatingUrl;
    private bool _disposed;

    internal DiffMarkdownRenderController(Panel host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    internal event EventHandler<(string Href, string? SourcePath)>? LinkClicked;

    internal bool IsActive => _viewModel?.IsMarkdownRenderActive == true;

    internal void Configure(IEditorSupportViewFactory factory, string previewFolder, string? instanceKey)
    {
        if (_disposed)
            return;
        _viewFactory = factory;
        var suffix = string.IsNullOrEmpty(instanceKey) ? "" : "-" + instanceKey;
        _navigation = new EditorSupportNavigationService(
            previewFolder, "preview-diff-" + Environment.ProcessId + suffix + ".html");
        var navigation = _navigation;
        _ = Task.Run(() => navigation.CleanStalePages(TimeSpan.FromDays(1)));
        _ = ApplyRenderAsync();
    }

    internal void SetViewModel(DiffSessionViewModel? viewModel)
    {
        if (_disposed)
            return;
        if (ReferenceEquals(_viewModel, viewModel))
            return;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _ = ApplyRenderAsync();
    }

    internal void ScrollToChange(int index)
    {
        if (_disposed)
            return;
        _pendingChange = index;
        FlushPendingChange();
    }

    internal void ReattachIfMoved()
    {
        if (_disposed)
            return;
        var window = Window.GetWindow(_host);
        if (!ReferenceEquals(_attachedWindow, window))
        {
            if (_attachedWindow is not null)
                _attachedWindow.Closed -= OnWindowClosed;
            _attachedWindow = window;
            if (_attachedWindow is not null)
                _attachedWindow.Closed += OnWindowClosed;
        }

        if (_web is null || ReferenceEquals(_webWindow, window))
            return;
        DisposeWeb();
        _ = ApplyRenderAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderSeq++;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        if (_attachedWindow is not null)
            _attachedWindow.Closed -= OnWindowClosed;
        _attachedWindow = null;
        DisposeWeb();
        LinkClicked = null;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DiffSessionViewModel.MarkdownRenderHtml)
            or nameof(DiffSessionViewModel.IsMarkdownRenderActive)))
            return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => _ = ApplyRenderAsync()));
            return;
        }
        _ = ApplyRenderAsync();
    }

    private void AbandonNavigation()
    {
        _navigatingUrl = null;
        _pageReady = true;
        FlushPendingChange();
    }

    private static bool IsSameUrl(string? actual, string expected)
        => Uri.TryCreate(actual, UriKind.Absolute, out var actualUri)
           && Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)
           && actualUri == expectedUri;

    private void FlushPendingChange()
    {
        if (_pendingChange < 0 || !_pageReady || _web.TryCore() is not { } core)
            return;
        var index = _pendingChange;
        _pendingChange = -1;
        try { _ = core.ExecuteScriptAsync(MarkdownDiffPage.ScrollToChangeScript(index)); }
        catch { /* WebView の再生成直後などは次の描画まで待つ */ }
    }

    private async Task ApplyRenderAsync()
    {
        if (_disposed)
            return;
        var sequence = ++_renderSeq;
        if (_viewModel is not { IsMarkdownRenderActive: true } vm)
            return;
        if (vm.MarkdownRenderHtml is not { Length: > 0 } html)
        {
            if (_web is not null)
                _web.Visibility = Visibility.Collapsed;
            return;
        }

        _pageReady = false;
        _navigatingUrl = null;
        var web = await EnsureWebAsync();
        if (sequence != _renderSeq)
            return;
        if (web is null || web.TryCore() is not { } core)
        {
            AbandonNavigation();
            return;
        }

        _navigation!.UpdatePreviewHost(core, vm.MarkdownRenderMapFolder);
        var navigation = _navigation;
        var url = await Task.Run(() => navigation.TryWritePage(html, out var written) ? written : null);
        if (sequence != _renderSeq)
            return;
        if (url is null || web.TryCore() is not { } current)
        {
            AbandonNavigation();
            return;
        }
        web.Visibility = Visibility.Visible;
        _navigatingUrl = url;
        try { current.Navigate(url); }
        catch { AbandonNavigation(); /* 前の表示はそのまま */ }
    }

    private async Task<WebView2CompositionControl?> EnsureWebAsync()
    {
        if (_viewFactory is null || _navigation is null)
            return null;
        if (_web is null)
        {
            _web = _viewFactory.Create();
            _host.Children.Insert(0, _web);
            _webWindow = Window.GetWindow(_host);
            _init = null;
        }
        var web = _web;
        var init = _init ??= InitCoreAsync(web);
        if (!await init)
        {
            if (ReferenceEquals(_init, init))
                _init = null;
            return null;
        }
        return ReferenceEquals(_web, web) ? web : null;
    }

    private async Task<bool> InitCoreAsync(WebView2CompositionControl web)
    {
        if (_viewFactory is null || !await _viewFactory.InitializeAsync(web))
            return false;
        if (web.TryCore() is not { } core)
            return false;

        _navigation!.ConfigureVirtualHosts(core, null);
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!EditorSupportNavigationService.IsPreviewUrl(e.Uri)
                && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationCompleted += (_, _) =>
        {
            if (!ReferenceEquals(_web, web) || _navigatingUrl is not { } expected
                || !IsSameUrl(core.Source, expected))
                return;
            _navigatingUrl = null;
            _pageReady = true;
            FlushPendingChange();
        };
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed)
                    return;
                DisposeWeb();
                _ = ApplyRenderAsync();
            }));
        };
        return true;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "linkClicked"
                && root.TryGetProperty("href", out var hrefElement)
                && hrefElement.GetString() is { Length: > 0 } href)
                LinkClicked?.Invoke(this, (href, _viewModel?.SelectedFile?.FullPath));
        }
        catch { /* 解釈できないメッセージは捨てる */ }
    }

    private void DisposeWeb()
    {
        if (_web is null)
            return;
        _host.Children.Remove(_web);
        _viewFactory?.Dispose(_web);
        _navigation?.ResetPreviewHost();
        _web = null;
        _init = null;
        _webWindow = null;
        _pageReady = false;
        _navigatingUrl = null;
    }
}
