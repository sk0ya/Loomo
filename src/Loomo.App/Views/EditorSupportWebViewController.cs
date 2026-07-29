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
    private string _searchTerm = "";
    private bool _searchCaseSensitive;
    private bool _searchUseRegex;
    private readonly SemaphoreSlim _findGate = new(1, 1);
    private string? _appliedFindTerm;          // 今の文書へ実際に適用済みの Find 条件（null＝不明・未適用）
    private bool _appliedFindCaseSensitive;

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

    /// <summary>プレビュー内で塗る検索ワードを設定する（空で消える）。条件は保持しておき、
    /// ページを組み直すたび（ナビゲーション完了・本文差し替え）に送り直す。</summary>
    public void SetSearchHighlight(string? term, bool caseSensitive, bool useRegex)
    {
        _searchTerm = term ?? "";
        _searchCaseSensitive = caseSensitive;
        _searchUseRegex = useRegex;
        if (View?.CoreWebView2 is { } core)
            PushSearchHighlight(core);
    }

    /// <summary>
    /// 現在の条件をページへ反映する。通常の HTML は注入スクリプト（CSS Custom Highlight API）で塗るが、
    /// <b>PDF は Chromium 内蔵ビューアの中身</b>でスクリプトが届かないので、WebView2 の Find API
    /// （＝Ctrl+F と同じページ内検索）を既定 UI 非表示で使う。Find API はリテラル検索なので、
    /// 正規表現モードのときは PDF を塗らない（誤った位置を塗るより塗らない方を選ぶ）。
    /// </summary>
    private void PushSearchHighlight(CoreWebView2 core)
    {
        if (!IsPdf(_navigatedUri))
        {
            _ = ApplyFindAsync(core, "", false);   // PDF から離れた直後の塗り残しを消す
            EditorSupportSearchHighlight.Post(core, _searchTerm, _searchCaseSensitive, _searchUseRegex);
            return;
        }
        // Find API はリテラル検索なので正規表現モードでは塗らない（誤った位置を塗るより塗らない方を選ぶ）。
        _ = ApplyFindAsync(core, _searchUseRegex ? "" : _searchTerm, _searchCaseSensitive);
    }

    /// <summary>
    /// Find セッションを目的の条件へ合わせる。<see cref="CoreWebView2Find.StartAsync"/> は非同期なので、
    /// 打鍵ごとの呼び出しが追い越し合うと「消したはずの語が塗られたまま」になる——<see cref="_findGate"/> で
    /// 直列化し、適用済みの条件（<see cref="_appliedFindTerm"/>）と同じ要求は捨てて、最後の要求が必ず勝つようにする。
    /// </summary>
    private async Task ApplyFindAsync(CoreWebView2 core, string term, bool caseSensitive)
    {
        await _findGate.WaitAsync();
        try
        {
            if (_appliedFindTerm == term && (term.Length == 0 || _appliedFindCaseSensitive == caseSensitive))
                return;
            if (term.Length == 0)
            {
                core.Find.Stop();   // セッションが無ければ何もしない（API 仕様）
                _appliedFindTerm = "";
                return;
            }
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = caseSensitive;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;   // 自前の検索パネルがあるので既定の検索バーは出さない
            await core.Find.StartAsync(options);
            _appliedFindTerm = term;
            _appliedFindCaseSensitive = caseSensitive;
        }
        catch
        {
            // Find API 非対応のランタイム等：塗られないだけ。適用済みは不明になるので次回やり直す。
            _appliedFindTerm = null;
        }
        finally
        {
            _findGate.Release();
        }
    }

    private static bool IsPdf(string? uri)
        => uri is not null && uri.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

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
                // 前のページの鍵を残すと、この URI の読み込み完了で ReadyPageKey がその鍵へ戻ってしまい、
                // 同じファイルへ戻ったときに「本文差し替えで足りる」と誤判定する（＝URI ページのまま更新されない）。
                _loadingPageKey = null;
            }
            catch { }
            return;
        }

        if (_pendingBody is { } body)
        {
            _navigatedUri = null;   // 本文差し替えが成り立つのは HTML ページのときだけ（URI ページではない）
            if (_pendingMapFolder is not null)
                _navigation.UpdatePreviewHost(core, _pendingMapFolder);
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setBody", html = body }));
            }
            catch { }
            // 差し替え後の本文へ塗り直す（メッセージは送った順に届くので setBody の後になる）。
            PushSearchHighlight(core);
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
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(EditorSupportSearchHighlight.Script); }
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
        _findGate.Dispose();
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
        // ページを組み直すとページ側の保持状態（と Find セッション）が消えるので、検索ハイライトを送り直す。
        if (e.IsSuccess && View?.CoreWebView2 is { } loaded)
        {
            _appliedFindTerm = null;
            PushSearchHighlight(loaded);
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
