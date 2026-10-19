using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ターミナルサポートの、中身を Web ページとして見せ直す面——Claude の会話を読み物として（§39.4）と、コマンドの出力を
/// 形に合わせて（§39.5.1）。WebView2 と一時ページの道具は EditorSupport と同じもの
/// （<see cref="IEditorSupportViewFactory"/>・<see cref="EditorSupportNavigationService"/>）を使い、ページは
/// <see cref="MarkdownPage"/>（テーマ・mermaid・アウトライン・本文差し替え）に乗せる。
/// <para><b>ページを組むのはテーマが変わったときだけ</b>。会話が書き足されるたびの更新は本文の差し替え（<c>setBody</c>）で、
/// ページは変わったターンだけを入れ替え、末尾を読んでいれば末尾に付いていく——読み返している途中のスクロール位置は飛ばない。</para>
/// </summary>
internal sealed class TerminalSupportWebPage : IDisposable
{
    private readonly Panel _host;
    private readonly Dispatcher _dispatcher;
    private readonly string _pageTag;
    private readonly string _title;
    private readonly bool _followTurns;
    private IEditorSupportViewFactory? _factory;
    private EditorSupportNavigationService? _navigation;
    private WebView2CompositionControl? _web;
    private Task<bool>? _init;
    private string? _pageKey;
    private string? _pendingBody;
    private string? _lastBody;
    private bool _pageReady;
    private bool _scrollToLatestOnLoad;
    private int _sequence;
    private bool _disposed;

    /// <param name="pageTag">一時ページの名前の一部（<c>preview-&lt;tag&gt;-&lt;pid&gt;.html</c>）。面ごとに別のページにする。</param>
    /// <param name="title">ページの題。</param>
    /// <param name="followTurns">会話のターンに付いていくか（開いたら最新のターンの頭・新しいターンへ付いていく、§39.4）。</param>
    public TerminalSupportWebPage(Panel host, Dispatcher dispatcher, string pageTag, string title, bool followTurns)
    {
        _host = host;
        _dispatcher = dispatcher;
        _pageTag = pageTag;
        _title = title;
        _followTurns = followTurns;
    }

    /// <summary>本文のリンクが押された（ファイル・URL。振り分けは EditorSupport のプレビューと同じ口へ）。</summary>
    public event EventHandler<string>? LinkClicked;

    public void Configure(IEditorSupportViewFactory factory, string previewFolder)
    {
        _factory = factory;
        _navigation = new EditorSupportNavigationService(previewFolder, "preview-" + _pageTag + "-" + Environment.ProcessId + ".html");
    }

    /// <summary>本文を出す。<paramref name="theme"/> が前回と同じなら本文だけ差し替える。同じ本文なら何もしない。
    /// <paramref name="fromTop"/> は別の中身に替わった（別のコマンドの出力を選んだ）——先頭から見せる。</summary>
    public async Task ShowAsync(string body, string theme, bool fromTop = false)
    {
        if (_disposed || _factory is null || _navigation is null)
            return;
        if (_pageKey == theme && body == _lastBody)
            return;
        _lastBody = body;
        if (_pageKey == theme)
        {
            if (_pageReady)
            {
                PostBody(body);
                if (fromTop)
                    Run(ScrollToTopScript);
                else if (_followTurns)
                    FollowNewTurn();
            }
            else
                _pendingBody = body;   // ページを組んでいる途中。終わったら最新の本文を流す
            return;
        }

        // テーマが変わった（初回も）：ページを組み直す。
        var sequence = ++_sequence;
        _pageKey = theme;
        _pageReady = false;
        _pendingBody = null;
        var web = await EnsureWebAsync();
        if (sequence != _sequence || web?.TryCore() is not { } core)
            return;
        var html = MarkdownPage.BuildPage(body, _title, theme, outline: true);
        _scrollToLatestOnLoad = _followTurns;
        var navigation = _navigation;
        var url = await Task.Run(() => navigation.TryWritePage(html, out var written) ? written : null);
        if (sequence != _sequence || url is null)
            return;
        try { core.Navigate(url); }
        catch { _pageKey = null; }   // 次の更新でもう一度組む
    }

    private void PostBody(string body)
    {
        if (_web.TryCore() is not { } core)
            return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setBody", html = body })); }
        catch { _pageKey = null; }
    }

    /// <summary>
    /// 最新のターンを読んでいた人だけ、新しく始まったターンへ連れていく。古いターンを読み返している人は動かさない
    /// （ページの本文差し替えは、末尾に居るときだけ末尾に付いていく——それとは別に「ターンの頭」で止めたい）。
    /// 本文差し替えはメッセージで届くので、それが当たってから判定する（少し待つ）。
    /// </summary>
    private void FollowNewTurn() => Run(FollowNewTurnScript);

    private void Run(string script)
    {
        if (_web.TryCore() is not { } core)
            return;
        try { _ = core.ExecuteScriptAsync(script); }
        catch { /* 再生成直後などは次の更新で */ }
    }

    /// <summary>開いた直後：最新のターンの頭を出す（会話の先頭から読ませない）。</summary>
    private const string ScrollToLatestScript = """
        (() => {
          const turns = document.querySelectorAll('section.cc-turn');
          window.__ccTurns = turns.length;
          if (turns.length) turns[turns.length - 1].scrollIntoView({ block: 'start' });
        })();
        """;

    /// <summary>別の中身に替えたとき：本文の差し替え（メッセージで届く）が当たってから先頭へ。</summary>
    private const string ScrollToTopScript = "setTimeout(() => window.scrollTo(0, 0), 60);";

    /// <summary>ターンが増えていて、ひとつ前（さっきまでの最新）のターンが画面に入っていれば、新しいターンの頭へ。</summary>
    private const string FollowNewTurnScript = """
        (() => {
          const before = window.__ccTurns ?? -1;
          setTimeout(() => {
            const turns = document.querySelectorAll('section.cc-turn');
            const count = turns.length;
            window.__ccTurns = count;
            if (before < 0 || count <= before) return;
            // 記録より先にページができていた（開いた直後は本文が空で、最新のターンへ寄せる相手がいなかった）。
            // 初めて会話が届いたこのときに、開いた直後と同じく最新のターンの頭を出す。
            if (before === 0) { turns[count - 1].scrollIntoView({ block: 'start' }); return; }
            const previous = turns[before - 1];
            if (!previous) return;
            const box = previous.getBoundingClientRect();
            if (box.bottom > 0 && box.top < window.innerHeight)
              turns[count - 1].scrollIntoView({ block: 'start', behavior: 'smooth' });
          }, 120);
        })();
        """;

    private async Task<WebView2CompositionControl?> EnsureWebAsync()
    {
        if (_web is null)
        {
            _web = _factory!.Create();
            _host.Children.Add(_web);
            _init = null;
        }
        var web = _web;
        var init = _init ??= InitAsync(web);
        if (!await init)
        {
            if (ReferenceEquals(_init, init))
                _init = null;
            return null;
        }
        return ReferenceEquals(_web, web) ? web : null;
    }

    private async Task<bool> InitAsync(WebView2CompositionControl web)
    {
        if (!await _factory!.InitializeAsync(web) || web.TryCore() is not { } core)
            return false;
        _navigation!.ConfigureVirtualHosts(core, null);
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!EditorSupportNavigationService.IsPreviewUrl(e.Uri) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationCompleted += (_, _) =>
        {
            if (!ReferenceEquals(_web, web))
                return;
            _pageReady = true;
            if (_pendingBody is { } pending)
            {
                _pendingBody = null;
                PostBody(pending);
            }
            if (_scrollToLatestOnLoad)
            {
                _scrollToLatestOnLoad = false;
                Run(ScrollToLatestScript);
            }
        };
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "linkClicked"
                    && doc.RootElement.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } target)
                    LinkClicked?.Invoke(this, target);
            }
            catch (JsonException) { }
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            _dispatcher.BeginInvoke(() =>
            {
                DisposeWeb();
                var body = _lastBody;
                _lastBody = null;
                if (body is not null && _pageKey is { } theme)
                {
                    _pageKey = null;
                    _ = ShowAsync(body, theme);   // 落ちたのは Chromium 側。中身は同じものを組み直す
                }
            });
        };
        return true;
    }

    private void DisposeWeb()
    {
        if (_web is null)
            return;
        _host.Children.Remove(_web);
        _factory?.Dispose(_web);
        _navigation?.ResetPreviewHost();
        _web = null;
        _init = null;
        _pageReady = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sequence++;
        DisposeWeb();
        LinkClicked = null;
    }
}
