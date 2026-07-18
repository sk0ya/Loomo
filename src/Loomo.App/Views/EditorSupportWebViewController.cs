namespace sk0ya.Loomo.App.Views;

/// <summary>EditorSupport 用 WebView2 の生成、描画状態、ナビゲーション、スクロール転送を管理する。</summary>
public sealed class EditorSupportWebViewController : IDisposable
{
    private readonly Panel _host;
    private readonly EditorSupportNavigationService _navigation;
    private readonly Func<CoreWebView2CreationProperties> _creationProperties;
    private readonly EventHandler<CoreWebView2WebMessageReceivedEventArgs> _messageReceived;
    private readonly EventHandler<CoreWebView2ContextMenuRequestedEventArgs> _contextMenuRequested;
    private readonly IEditorSupportViewFactory _viewFactory;
    private Task<bool>? _initTask;
    private bool _eventsAttached;
    private bool _firstRenderHealed;
    private string? _pendingHtml;
    private string? _pendingBody;
    private string? _pendingPageKey;
    private string? _pendingUri;
    private string? _pendingMapFolder;
    private string? _loadingPageKey;
    private string? _navigatedUri;

    public EditorSupportWebViewController(
        Panel host,
        EditorSupportNavigationService navigation,
        Func<CoreWebView2CreationProperties> creationProperties,
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> messageReceived,
        EventHandler<CoreWebView2ContextMenuRequestedEventArgs> contextMenuRequested,
        IEditorSupportViewFactory viewFactory)
    {
        _host = host;
        _navigation = navigation;
        _creationProperties = creationProperties;
        _messageReceived = messageReceived;
        _contextMenuRequested = contextMenuRequested;
        _viewFactory = viewFactory;
    }

    public WebView2CompositionControl? View { get; private set; }
    public IEditorSupportViewFactory ViewFactory => _viewFactory;
    public string? ReadyPageKey { get; private set; }
    public event EventHandler? NavigationCompleted;

    public void SetPending(string? html, string? body, string? uri, string? mapFolder, string? pageKey)
    {
        _pendingHtml = html;
        _pendingBody = body;
        _pendingUri = uri;
        _pendingMapFolder = mapFolder;
        _pendingPageKey = pageKey;
    }

    public void ResetPageState()
    {
        ReadyPageKey = null;
        _loadingPageKey = null;
    }

    public async Task<WebView2CompositionControl?> EnsureAsync()
    {
        if (View is null)
        {
            View = _viewFactory.Create(_creationProperties());
            View.NavigationCompleted += OnNavigationCompleted;
            _host.Children.Add(View);
        }

        _initTask ??= InitializeCoreAsync(View);
        if (!await _initTask)
        {
            _initTask = null;
            return null;
        }
        return View;
    }

    public void RenderPending(CoreWebView2 core)
    {
        if (_pendingUri is { } uri)
        {
            if (string.Equals(uri, _navigatedUri, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                core.Navigate(uri);
                _navigatedUri = uri;
                ReadyPageKey = null;
            }
            catch { }
            return;
        }

        if (_pendingBody is { } body)
        {
            if (_pendingMapFolder is not null)
                _navigation.UpdatePreviewHost(core, _pendingMapFolder);
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setBody", html = body }));
            }
            catch { }
            return;
        }

        if (_pendingHtml is null)
            return;
        _navigatedUri = null;
        if (_pendingMapFolder is not null)
            _navigation.UpdatePreviewHost(core, _pendingMapFolder);
        ReadyPageKey = null;
        _loadingPageKey = _pendingPageKey;

        if (_navigation.TryWritePage(_pendingHtml, out var pageUrl))
        {
            try { core.Navigate(pageUrl); }
            catch { _loadingPageKey = null; }
            return;
        }
        try { core.NavigateToString(_pendingHtml); }
        catch { _loadingPageKey = null; }
    }

    public bool TryHorizontalScroll(int delta)
    {
        if (delta == 0 || View is not { Visibility: Visibility.Visible, IsMouseOver: true, CoreWebView2: { } core })
            return false;
        try
        {
            core.PostWebMessageAsJson(FormattableString.Invariant($"{{\"type\":\"hscroll\",\"dx\":{delta}}}"));
            return true;
        }
        catch { return false; }
    }

    public void PostScrollRatio(double ratio)
    {
        if (View?.CoreWebView2 is not { } core)
            return;
        try
        {
            core.PostWebMessageAsJson(FormattableString.Invariant(
                $"{{\"type\":\"setScrollRatio\",\"ratio\":{Math.Clamp(ratio, 0.0, 1.0):R}}}"));
        }
        catch { }
    }

    private async Task<bool> InitializeCoreAsync(WebView2CompositionControl view)
    {
        if (!await _viewFactory.InitializeAsync(view))
            return false;
        if (view.CoreWebView2 is not { } core)
            return false;
        if (!_eventsAttached)
        {
            core.WebMessageReceived += _messageReceived;
            core.ContextMenuRequested += _contextMenuRequested;
            _navigation.ConfigureVirtualHosts(core, null);
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(HorizontalScrollScript); }
            catch { }
            _eventsAttached = true;
        }
        return true;
    }

    public void Dispose()
    {
        if (View is not null)
            View.NavigationCompleted -= OnNavigationCompleted;
        if (_eventsAttached && View?.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= _messageReceived;
            core.ContextMenuRequested -= _contextMenuRequested;
        }
        _viewFactory.Dispose(View);
        View = null;
        _initTask = null;
        _eventsAttached = false;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess && _navigatedUri is not null)
            _navigatedUri = null;
        if (e.IsSuccess)
            ReadyPageKey = _loadingPageKey;
        if (!_firstRenderHealed && View?.CoreWebView2 is { } core)
        {
            _firstRenderHealed = true;
            RenderPending(core);
        }
        NavigationCompleted?.Invoke(this, EventArgs.Empty);
    }

    private const string HorizontalScrollScript = """
        (() => {
            let mx = 0, my = 0;
            addEventListener('mousemove', e => { mx = e.clientX; my = e.clientY; }, true);
            function scrollableX(el) {
                for (; el && el.nodeType === 1; el = el.parentElement) {
                    if (el.scrollWidth > el.clientWidth) {
                        const ox = getComputedStyle(el).overflowX;
                        if (ox === 'auto' || ox === 'scroll') return el;
                    }
                }
                return document.scrollingElement || document.documentElement;
            }
            window.chrome?.webview?.addEventListener('message', e => {
                const d = e.data;
                if (d && d.type === 'hscroll') {
                    const el = scrollableX(document.elementFromPoint(mx, my));
                    if (el) el.scrollLeft += d.dx;
                }
            });
        })();
        """;
}
