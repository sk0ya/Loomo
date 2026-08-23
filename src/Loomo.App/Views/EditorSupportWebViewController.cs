namespace sk0ya.Loomo.App.Views;

/// <summary>いま WebView2 に載せているページの同一性。URI 直開きと生成 HTML の両方を1つで表す。</summary>
internal sealed record EditorSupportPageId(
    string? Uri,
    string? PageKey,
    /// <summary>同じURLを WebView2 が再読み込みできるページか。</summary>
    bool CanReload = true);

/// <summary>ページ読み込みの状態。</summary>
internal enum EditorSupportPageStatus
{
    /// <summary>何も載せていない。</summary>
    Idle,

    /// <summary>ナビゲート要求済み・完了待ち。</summary>
    Loading,

    /// <summary>読み込み完了。本文差し替え（setBody）が成り立つのはこの状態のときだけ。</summary>
    Ready,

    /// <summary>失敗・中断・応答なし。同一性は必ず捨てられ、次の要求は必ず作り直しになる。</summary>
    Failed,
}

/// <summary><see cref="EditorSupportWebViewController.Show"/> の結果。</summary>
internal enum EditorSupportPageApplyResult
{
    /// <summary>要求どおり反映した（ナビゲート・本文差し替え・同一ページなので据え置き）。</summary>
    Applied,

    /// <summary>本文差し替えを頼まれたが差し替え先のページがもう無い。呼び元がページ全体を組み直すこと。</summary>
    NeedsFullPage,
}

/// <summary>EditorSupport 用 WebView2 の生成、描画状態、ナビゲーション、スクロール転送を管理する。</summary>
public sealed class EditorSupportWebViewController : IDisposable
{
    /// <summary>
    /// ナビゲート要求から <c>NavigationCompleted</c> を待つ上限。これを過ぎたら
    /// <see cref="EditorSupportPageStatus.Failed"/> にして <see cref="ReloadRequested"/> を上げる。
    /// WebView2 の完了イベントは（プロセス落ち・不正 URI・描画中断などで）<b>来ないことがある</b>。
    /// 以前は来なければ状態が <c>Loading</c> のまま固まり、同じページを二度と読み直さなかった。
    /// </summary>
    private static readonly TimeSpan NavigationWatchdog = TimeSpan.FromSeconds(12);

    /// <summary>WebView2 を用意できなかったときのやり直し間隔（使い切ったら利用者へ知らせる）。</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    private readonly Panel _host;
    private readonly EditorSupportNavigationService _navigation;
    private readonly Func<CoreWebView2CreationProperties> _creationProperties;
    private readonly EventHandler<CoreWebView2WebMessageReceivedEventArgs> _messageReceived;
    private readonly EventHandler<CoreWebView2ContextMenuRequestedEventArgs> _contextMenuRequested;
    private readonly IEditorSupportViewFactory _viewFactory;
    /// <summary>いま何が載っているかの判断は全部こちら（テストできる形に切り出してある）。</summary>
    private readonly EditorSupportPageState _page = new();
    private Task<bool>? _initTask;
    private bool _eventsAttached;
    private DispatcherTimer? _watchdog;
    private DispatcherTimer? _retryTimer;
    private int _retryAttempt;
    private string _searchTerm = "";
    private bool _searchCaseSensitive;
    private bool _searchUseRegex;
    private string? _markdownSource;
    private bool _markdownEditMode;
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

    /// <summary>いまの CoreWebView2（未生成・ブラウザプロセスが落ちた後は null）。素の
    /// <c>View.CoreWebView2</c> は落ちた後に<b>読むだけで例外</b>を投げるので、参照は必ずここを通す
    /// （<see cref="WebViewSafe.TryCore"/>）。</summary>
    public CoreWebView2? Core => View.TryCore();
    public IEditorSupportViewFactory ViewFactory => _viewFactory;

    /// <summary>
    /// 本文差し替えで更新できるページの鍵。<b>読み込みが完了しているときだけ</b>返す。
    /// <c>Loading</c>／<c>Failed</c>／<c>Idle</c> では null＝呼び元は必ずページ全体を組み立てる。
    /// </summary>
    public string? ReadyPageKey => _page.ReadyPageKey;

    public event EventHandler? NavigationCompleted;

    /// <summary>
    /// 表示が行き詰まったので描き直してほしい（ナビゲーション失敗・応答なし・初回描画の取りこぼし）。
    /// ホストは EditorSupport の更新ループへ <c>Invalidate</c> を投げ、ページ全体を組み直す。
    /// </summary>
    public event EventHandler? ReloadRequested;

    /// <summary>
    /// 生成済みHTMLを一時ページへUIスレッド外で書き込む。フレーム適用時は完成したURLを
    /// Navigateするだけにして、大きなプレビューの同期ファイルI/Oで画面を止めない。
    /// </summary>
    internal Task<string?> PreparePageAsync(string html, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _navigation.TryWritePage(html, out var url) ? url : null;
        }, ct);

    public void ResetPageState()
    {
        StopWatchdog();
        _page.Reset();
    }

    /// <summary>プレビュー内で塗る検索ワードを設定する（空で消える）。条件は保持しておき、
    /// ページを組み直すたび（ナビゲーション完了・本文差し替え）に送り直す。</summary>
    public void SetSearchHighlight(string? term, bool caseSensitive, bool useRegex)
    {
        _searchTerm = term ?? "";
        _searchCaseSensitive = caseSensitive;
        _searchUseRegex = useRegex;
        if (Core is { } core)
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
        if (!IsPdf(_page.CurrentUri))
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

    /// <summary>
    /// WebView2 を用意する。<b>生成に失敗したコントロールは捨てて作り直す</b>——失敗した WebView2 を握ったまま
    /// 再試行しても二度と立ち上がらないので、ページを組み直せる状態にならない。
    /// <para>失敗の現実的な原因は「別の Loomo が同じプロファイルを違うブラウザ引数で握っている」
    /// （<c>ERROR_INVALID_STATE 0x8007139F</c>、§21.5.3）なので、まずポートを引き当て直して作り直す。
    /// それでも駄目なら <see cref="ScheduleRetry"/> で数回やり直し、使い切ったら<b>黙らずに</b>知らせる——
    /// 握り潰していたせいで、症状が「ヘッダーだけ更新されて中身が永久に描かれない」になっていた。</para>
    /// </summary>
    public async Task<WebView2CompositionControl?> EnsureAsync()
    {
        if (await TryCreateViewAsync())
            return View;
        CodeSupportDiag.Log("editor support webview: 生成に失敗");
        if (WebViewEnvironment.TryRecover() && await TryCreateViewAsync())
        {
            CodeSupportDiag.Log("editor support webview: ポートを引き当て直して復帰");
            return View;
        }
        ScheduleRetry();
        return null;
    }

    private async Task<bool> TryCreateViewAsync()
    {
        if (View is null)
        {
            View = _viewFactory.Create(_creationProperties());
            View.NavigationCompleted += OnNavigationCompleted;
            _host.Children.Add(View);
        }

        _initTask ??= InitializeCoreAsync(View);
        if (await _initTask)
        {
            WebViewEnvironment.NoteCreated();
            StopRetry();
            return true;
        }
        DiscardView();
        return false;
    }

    /// <summary>
    /// 作り直しを待って仕掛ける。生成の失敗は<b>そのときだけのもの</b>でありうる——共有ブラウザプロセスの
    /// 落ち際や、別インスタンスが同時に立ち上げている最中に当たると失敗する。1回で諦めると、そのペインは
    /// 触り直すまで空のまま残る（これが「更新されない」の見え方）。数回だけ間を空けて自力でやり直し、
    /// 使い切ったら黙らずに知らせる。
    /// </summary>
    private void ScheduleRetry()
    {
        if (_retryAttempt >= RetryDelays.Length)
        {
            WebViewEnvironment.ReportUnavailable("エディタ支援");
            return;
        }
        var delay = RetryDelays[_retryAttempt++];
        CodeSupportDiag.Log($"editor support webview: {delay.TotalSeconds}秒後にやり直す（{_retryAttempt}回目）");
        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Stop();
        _retryTimer.Interval = delay;
        _retryTimer.Start();
    }

    private DispatcherTimer CreateRetryTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReloadRequested?.Invoke(this, EventArgs.Empty);
        };
        return timer;
    }

    private void StopRetry()
    {
        _retryTimer?.Stop();
        _retryAttempt = 0;
    }

    /// <summary>
    /// ブラウザプロセスが落ちた。<b>プロファイルを共有している以上、落ちる理由は自分とは限らない</b>——
    /// Loomo を2つ起動していれば共有ブラウザプロセスは1つなので、他インスタンス側の巻き添えでも落ちる。
    /// この WebView2 はもう二度と描かないので捨てて、ホストにページごと組み直させる（放っておくと
    /// 「ヘッダーだけ更新されて中身が空のまま」になる）。描画プロセスだけの死は読み直しで戻る。
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        CodeSupportDiag.Log($"editor support webview: プロセス落ち {e.ProcessFailedKind}");
        var browserExited = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        // WebView2 が自分のイベントを配っている最中にコントロールを壊さない（次のディスパッチへ回す）。
        _host.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (browserExited)
                    DiscardView();
            }
            catch (Exception ex)
            {
                // 死んだ WebView2 の後始末は例外を投げうる。ここで抜けると立て直しの合図まで届かない。
                CodeSupportDiag.Log($"editor support webview: 後始末で例外 {ex}");
            }
            CodeSupportDiag.Log("editor support webview: 組み直しを要求");
            ReloadRequested?.Invoke(this, EventArgs.Empty);
        }));
    }

    /// <summary>使えなくなった WebView2 を捨てる（次の <see cref="EnsureAsync"/> が作り直す）。</summary>
    private void DiscardView()
    {
        ResetPageState();
        // 仮想ホストのマップは core に付いているので、捨てたら「マップ済み」の記憶も捨てる
        // （さもないと作り直した core に preview.loomo が無いまま張り直されない）。
        _navigation.ResetPreviewHost();
        if (View is not null)
        {
            View.NavigationCompleted -= OnNavigationCompleted;
            DetachCoreHandlers(Core);
            _host.Children.Remove(View);
        }
        _viewFactory.Dispose(View);
        View = null;
        _initTask = null;
        _eventsAttached = false;
    }

    /// <summary>
    /// 完成したフレームの WebView 部分を反映する。<b>遷移はすべてここ1か所</b>で、
    /// 失敗経路（例外・ナビゲーション失敗・応答なし）は必ず <see cref="Fail"/> を通って
    /// ページの同一性を捨てる——「ガードが立ったままで二度と読み直さない」を構造的に潰すため。
    /// </summary>
    internal EditorSupportPageApplyResult Show(CoreWebView2 core, EditorSupportFrameContent.WebContent content)
    {
        _markdownSource = content.MarkdownSource;
        if (_markdownSource is null)
            _markdownEditMode = false;
        if (content.Uri is { } uri)
            return ShowUri(core, uri);
        if (content.Body is { } body)
            return PatchBody(core, body, content.MapFolder, content.PageKey);
        if (content.Html is { } html)
            return ShowHtml(core, html, content.MapFolder, content.PageKey, content.PreparedPageUrl);
        return EditorSupportPageApplyResult.Applied;   // 表示するものが無い（呼び元が組み立てていない）
    }

    private EditorSupportPageApplyResult ShowUri(CoreWebView2 core, string uri)
    {
        if (_page.IsShowing(uri))
        {
            PushSearchHighlight(core);
            return EditorSupportPageApplyResult.Applied;
        }

        BeginLoad(new EditorSupportPageId(uri, null));
        try { core.Navigate(uri); }
        catch { Fail(); }
        return EditorSupportPageApplyResult.Applied;
    }

    /// <summary>
    /// いま <paramref name="uri"/> を<b>読み終えて</b>載せているなら、その場で読み直す（§24.8）。
    /// <para>
    /// <see cref="Show"/> は<b>同じ URI なら再ナビゲートを省く</b>（<c>IsShowing</c>）ので、
    /// ディスク上の更新を反映させたいだけの要求は通常の描画経路では素通りしてしまう。ここは
    /// 「同じページのまま読み直す」ための別口で、初回描画の取りこぼし対策と同じ
    /// <see cref="ReloadCurrentPage"/>（ページの同一性は保ったまま <c>Reload()</c>＋応答監視、
    /// 例外なら <see cref="Fail"/>）を通す——HTML の再生成も一時ファイルの書き直しも要らないうえ、
    /// 失敗の畳み方が既存の経路とずれない。
    /// </para>
    /// <para>
    /// false＝そのページを載せていない（読み込み中・失敗後・別ページ）。読み直しでは直らないので、
    /// 呼び元はページ全体の組み直しへ回すこと。
    /// </para>
    /// </summary>
    internal bool ReloadShowing(string uri)
        => _page.IsShowing(uri) && ReloadCurrentPage(Core);

    private EditorSupportPageApplyResult PatchBody(
        CoreWebView2 core, string body, string? mapFolder, string? pageKey)
    {
        if (!_page.CanPatchBody(pageKey))
            return EditorSupportPageApplyResult.NeedsFullPage;

        if (mapFolder is not null)
            _navigation.UpdatePreviewHost(core, mapFolder);
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setBody", html = body }));
            PostMarkdownState(core);
        }
        catch
        {
            Fail();
            return EditorSupportPageApplyResult.NeedsFullPage;
        }
        // 差し替え後の本文へ塗り直す（メッセージは送った順に届くので setBody の後になる）。
        PushSearchHighlight(core);
        return EditorSupportPageApplyResult.Applied;
    }

    private EditorSupportPageApplyResult ShowHtml(
        CoreWebView2 core, string html, string? mapFolder, string? pageKey, string? preparedPageUrl)
    {
        if (mapFolder is not null)
            _navigation.UpdatePreviewHost(core, mapFolder);

        if (preparedPageUrl is { })
        {
            BeginLoad(new EditorSupportPageId(null, pageKey, CanReload: true));
            try { core.Navigate(preparedPageUrl); }
            catch { Fail(); }
            return EditorSupportPageApplyResult.Applied;
        }

        if (_navigation.TryWritePage(html, out var pageUrl))
        {
            BeginLoad(new EditorSupportPageId(null, pageKey, CanReload: true));
            try { core.Navigate(pageUrl); }
            catch { Fail(); }
            return EditorSupportPageApplyResult.Applied;
        }
        // ファイル書き込みに失敗した場合の NavigateToString は、Reload() で復元できない
        // インメモリページ。初回の取りこぼし対策は従来どおりフル再構築へ戻す。
        BeginLoad(new EditorSupportPageId(null, pageKey, CanReload: false));
        try { core.NavigateToString(html); }
        catch { Fail(); }
        return EditorSupportPageApplyResult.Applied;
    }

    /// <summary>Markdownプレビューの同じWebViewをソース編集面へ切り替える。</summary>
    internal void SetMarkdownEditMode(bool enabled)
    {
        _markdownEditMode = enabled && _markdownSource is not null;
        if (Core is { } core)
            PostMarkdownState(core);
    }

    private void PostMarkdownState(CoreWebView2 core)
    {
        if (_markdownSource is null)
            return;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "setMarkdownEditMode",
                enabled = _markdownEditMode,
                source = _markdownSource
            }));
        }
        catch { }
    }

    private void BeginLoad(EditorSupportPageId id)
    {
        _page.BeginLoad(id);
        StartWatchdog();
    }

    /// <summary>読み込みが成立しなかった。<b>同一性を必ず捨てる</b>ので、次の要求は必ず作り直しになる。</summary>
    private void Fail()
    {
        StopWatchdog();
        _page.Fail();
    }

    private void StartWatchdog()
    {
        _watchdog ??= CreateWatchdog();
        _watchdog.Stop();
        _watchdog.Start();
    }

    private DispatcherTimer CreateWatchdog()
    {
        var timer = new DispatcherTimer { Interval = NavigationWatchdog };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_page.WatchdogFired() == EditorSupportPageAction.RequestReload)
                ReloadRequested?.Invoke(this, EventArgs.Empty);
        };
        return timer;
    }

    private void StopWatchdog() => _watchdog?.Stop();

    public bool TryHorizontalScroll(int delta)
    {
        if (delta == 0 || View is not { Visibility: Visibility.Visible, IsMouseOver: true } || Core is not { } core)
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
        if (Core is not { } core)
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
        if (view.TryCore() is not { } core)
            return false;
        if (!_eventsAttached)
        {
            core.WebMessageReceived += _messageReceived;
            core.ContextMenuRequested += _contextMenuRequested;
            core.ProcessFailed += OnProcessFailed;
            _navigation.ConfigureVirtualHosts(core, null);
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(HorizontalScrollScript); }
            catch { }
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(EditorSupportSearchHighlight.Script); }
            catch { }
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(EditorSupportContextLink.Script); }
            catch { }
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(PochiHostFlagScript); }
            catch { }
            _eventsAttached = true;
        }
        return true;
    }

    public void Dispose()
    {
        StopWatchdog();
        _watchdog = null;
        StopRetry();
        _retryTimer = null;
        ResetPageState();
        if (View is not null)
            View.NavigationCompleted -= OnNavigationCompleted;
        DetachCoreHandlers(Core);
        _viewFactory.Dispose(View);
        View = null;
        _initTask = null;
        _eventsAttached = false;
        _page.ResetFirstRenderHealing();   // 次に張り直すビューでも初回描画の取りこぼしを直す
        _findGate.Dispose();
    }

    private void DetachCoreHandlers(CoreWebView2? core)
    {
        if (!_eventsAttached || core is null)
            return;
        core.WebMessageReceived -= _messageReceived;
        core.ContextMenuRequested -= _contextMenuRequested;
        core.ProcessFailed -= OnProcessFailed;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        StopWatchdog();
        var action = _page.Completed(e.IsSuccess);

        // ページを組み直すとページ側の保持状態（と Find セッション）が消えるので、検索ハイライトを送り直す。
        if (e.IsSuccess && Core is { } loaded)
        {
            _appliedFindTerm = null;
            PushSearchHighlight(loaded);
            PostMarkdownState(loaded);
        }
        NavigationCompleted?.Invoke(this, EventArgs.Empty);

        if (action == EditorSupportPageAction.ReloadCurrentPage)
        {
            if (!ReloadCurrentPage(Core))
                ReloadRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (action == EditorSupportPageAction.RequestReload)
            ReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 初回描画の取りこぼし対策。同じページを再利用するので、HTMLの再生成・一時ファイルの
    /// 再書き込み・ページURLの作り直しを行わずに WebView2 だけを再読込する。
    /// </summary>
    private bool ReloadCurrentPage(CoreWebView2? core)
    {
        if (core is null || !_page.BeginCurrentPageReload())
            return false;
        StartWatchdog();
        try
        {
            core.Reload();
            return true;
        }
        catch
        {
            Fail();
            return false;
        }
    }

    /// <summary>
    /// ペインへ載せた Pochi（<see cref="PochiEditorSupport"/>）へ「WebView2 ホストの中にいる」と先に伝える印。
    /// Pochi は公開ビルド（リモート）なので、<c>window.chrome.webview</c> がページのモジュール読み込みより
    /// 一拍遅れて生えることがあり、その場合ブリッジは「web ビルド」と誤認して固定される。この印があれば
    /// Pochi 側（main.tsx）は chrome.webview の出現を待ってからブリッジを初期化する。他のプレビューページは
    /// この変数を読まないので無害。
    /// </summary>
    private const string PochiHostFlagScript = "window.__pochiHost = true;";

    /// <summary>
    /// 横ホイールをページへ流すスクリプト。<b>スクロールで動かせる要素があれば scrollLeft を動かし、
    /// 無ければ合成 wheel イベントを投げる</b>という二段構えなのが要点。
    ///
    /// 前者だけだと、<see cref="PochiEditorSupport"/> のキャンバスのように「スクロールコンテナを持たず
    /// ネイティブ wheel の deltaX を自分で捌いて描画をパンする」作りのページで<b>何も起きない</b>。しかも
    /// 呼び元（<c>TryHorizontalScroll</c>）は送信できた時点で成功を返して WM_MOUSEHWHEEL を handled に
    /// するので、汎用のネイティブ入力経路（<see cref="WebViewHorizontalWheel"/>）へも落ちてこない
    /// ——「ブラウザでは効くのに Loomo のペインでだけ横スクロールが死ぬ」のがこれだった。
    /// 合成イベントは既定動作を持たないので、スクロールできるページで二重に動く心配は無い。
    /// </summary>
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
                // 文書ルートは overflowX が visible でも横スクロールするので overflow は見ない。
                // 逆にあふれていなければ null を返す＝「スクロールでは動かせないページ」として扱う。
                const root = document.scrollingElement || document.documentElement;
                return root && root.scrollWidth > root.clientWidth ? root : null;
            }
            window.chrome?.webview?.addEventListener('message', e => {
                const d = e.data;
                if (!d || d.type !== 'hscroll') return;
                const target = document.elementFromPoint(mx, my);
                const el = scrollableX(target);
                if (el) {
                    el.scrollLeft += d.dx;
                    return;
                }
                // ページが自前で wheel を捌く作りのとき用。deltaX の符号は WM_MOUSEHWHEEL と同じ
                // （正＝右）で、ブラウザが本物の横ホイールで渡すのと揃う。
                (target ?? document.body)?.dispatchEvent(new WheelEvent('wheel', {
                    deltaX: d.dx, deltaY: 0, deltaMode: 0,
                    clientX: mx, clientY: my, bubbles: true, cancelable: true
                }));
            });
        })();
        """;
}
