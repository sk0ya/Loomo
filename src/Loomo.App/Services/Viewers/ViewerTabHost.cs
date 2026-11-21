using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>ビューア 1 つがいまどう見えるか。</summary>
internal enum ViewerTabStatus
{
    /// <summary>まだ読んでいない。</summary>
    Pending,
    /// <summary><c>viewer.json</c> が読めない（<see cref="ViewerTabHost.Error"/> に理由）。</summary>
    Broken,
    /// <summary>口とコマンドを人間がまだ許していない（または変わった）。ページは読み込まない。</summary>
    NeedsApproval,
    /// <summary>ページが出ている。</summary>
    Running,
}

/// <summary>
/// ビューア 1 つ＝WebView2 1 つ（§44.2）。ビューアのフォルダーをビューアだけのオリジン（<c>https://&lt;id&gt;.viewer.loomo</c>）で答え、
/// ページに <c>window.loomo</c> を差し込み、届いた頼みを <see cref="ViewerRoomApi"/> へ渡す。
/// <para><b>線引き</b>：許される前はページを読み込まない／頼みはビューアのオリジンから届いたものだけ聞く／ビューアの外への遷移は
/// させない（http(s) のリンクはブラウザペインへ）／<c>net</c> を挙げていないビューアの外向きの要求は全部断る／
/// ページが替わる・ビューアを閉じるときは走っているコマンドを止める。</para>
/// </summary>
internal sealed class ViewerTabHost : IDisposable
{
    private readonly ViewerStore _store;
    private readonly ViewerRoomState _state;
    private readonly ViewerRoomApi _api;
    private readonly IEditorSupportViewFactory _factory;
    private readonly Panel _host;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, Task> _openUrl;
    private readonly DispatcherTimer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private WebView2CompositionControl? _web;
    private Task<bool>? _init;
    private CancellationTokenSource _pageCancel = new();
    private string? _themeScriptId;
    private bool _visible;
    private bool _loadPending;
    private readonly List<(CancellationToken Cancel, string Json)> _pendingEvents = [];
    private bool _disposed;

    public ViewerTabHost(string id, ViewerStore store, ViewerRoomState state, ViewerRoomApi api,
        IEditorSupportViewFactory factory, Panel host, Dispatcher dispatcher, Func<string, Task> openUrl)
    {
        Id = id;
        _store = store;
        _state = state;
        _api = api;
        _factory = factory;
        _host = host;
        _dispatcher = dispatcher;
        _openUrl = openUrl;
        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            _ = RefreshAsync(reload: true);
        };
        // 隠れているペインの中では WebView2 の初期化が終わらない（待つと、ビューアを出した側＝MCP の呼び出しが返らない）。
        // 読み込みは見えたときに回す。
        _host.IsVisibleChanged += OnHostVisibleChanged;
        Watch();
    }

    private void OnHostVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            LoadIfPending();
    }

    private void LoadIfPending()
    {
        if (_loadPending && _visible && _host.IsVisible && !_disposed)
            _ = RefreshAsync(reload: true);
    }

    public string Id { get; }
    public ViewerManifest? Manifest { get; private set; }
    public string? Error { get; private set; }
    public ViewerTabStatus Status { get; private set; } = ViewerTabStatus.Pending;
    public string Title => Manifest?.Title ?? Id;

    /// <summary>見え方（題・状態）が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>定義を読み直して、出せるなら出す。<paramref name="reload"/> はフォルダーの中身が変わったとき。</summary>
    public async Task RefreshAsync(bool reload = false)
    {
        if (_disposed)
            return;
        var entry = await Task.Run(() => _store.Find(Id));
        if (_disposed)
            return;
        Manifest = entry.Manifest;
        Error = entry.Error;
        if (entry.Manifest is not { } manifest)
        {
            Unload();
            SetStatus(ViewerTabStatus.Broken);
            return;
        }
        if (manifest.NeedsApproval && !_state.IsApproved(manifest))
        {
            Unload();
            SetStatus(ViewerTabStatus.NeedsApproval);
            return;
        }
        var wasRunning = Status == ViewerTabStatus.Running && _web is not null;
        if (!_visible || !_host.IsVisible)
        {
            // 前に出ていない・ペインが隠れている：許しは済んでいるので「出せる」とだけ言い、読み込みは見えたときに。
            _loadPending = true;
            SetStatus(ViewerTabStatus.Running);
            return;
        }
        _loadPending = false;
        SetStatus(ViewerTabStatus.Running);
        if (await EnsureWebAsync() is not { } web || web.TryCore() is not { } core)
        {
            Error = "WebView2 could not be started.";
            SetStatus(ViewerTabStatus.Broken);
            return;
        }
        if (wasRunning && !reload && ViewerUrl.IsOf(Id, web.TryUrl()))
            return;
        CancelPage();
        await ApplyThemeScriptAsync(core);
        if (wasRunning && ViewerUrl.IsOf(Id, web.TryUrl()))
            core.Reload();
        else
            core.Navigate(ViewerUrl.Index(Id));
    }

    /// <summary>題だけ先に読む（復元したタブ。前に出るまでページも許しのカードも作らない）。</summary>
    public async Task PeekAsync()
    {
        var entry = await Task.Run(() => _store.Find(Id));
        if (_disposed || Status != ViewerTabStatus.Pending)
            return;
        Manifest = entry.Manifest;
        Error = entry.Error;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>人間が口とコマンドを許した。</summary>
    public Task ApproveAsync()
    {
        if (Manifest is { } manifest)
            _state.Approve(manifest);
        return RefreshAsync();
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_web is not null)
            _web.Visibility = visible && Status == ViewerTabStatus.Running ? Visibility.Visible : Visibility.Collapsed;
        LoadIfPending();
    }

    public void Focus() => _web?.Focus();

    /// <summary>部屋の出来事をページへ（<c>loomo.on</c>）。その口を挙げていないビューアには流さない。</summary>
    public void Emit(string name, JsonNode? data, string requiredApi)
    {
        if (Status != ViewerTabStatus.Running || Manifest?.Allows(requiredApi) != true)
            return;
        Post(new JsonObject { ["k"] = "event", ["name"] = name, ["data"] = data });
    }

    private void SetStatus(ViewerTabStatus status)
    {
        Status = status;
        SetVisible(_visible);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelPage()
    {
        _pageCancel.Cancel();
        _pageCancel.Dispose();
        _pageCancel = new CancellationTokenSource();
    }

    private void Unload()
    {
        CancelPage();
        if (_web is null)
            return;
        _host.Children.Remove(_web);
        _factory.Dispose(_web);
        _web = null;
        _init = null;
        _themeScriptId = null;
    }

    private async Task<WebView2CompositionControl?> EnsureWebAsync()
    {
        if (_web is null)
        {
            _web = _factory.Create();
            _web.Visibility = _visible ? Visibility.Visible : Visibility.Collapsed;
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
        if (!await _factory.InitializeAsync(web) || web.TryCore() is not { } core)
            return false;
        core.Settings.IsStatusBarEnabled = false;
        // ビューアのフォルダーをビューアのオリジンで答え、ほかの行き先は（net を挙げていなければ）全部断る。
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (ViewerUrl.IsOf(Id, e.Uri) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;
            e.Cancel = true;   // ビューアの WebView はビューアのページだけを出す
            if (e.IsUserInitiated && IsWebUrl(e.Uri))
                _ = _openUrl(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated && IsWebUrl(e.Uri))
                _ = _openUrl(e.Uri);
        };
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            _dispatcher.BeginInvoke(() =>
            {
                Unload();
                Status = ViewerTabStatus.Pending;
                _ = RefreshAsync();   // 落ちたのは Chromium 側。同じビューアを組み直す
            });
        };
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(ViewerBridgeScript.Script); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
        return true;
    }

    private static bool IsWebUrl(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
           && !parsed.Host.EndsWith(ViewerUrl.HostSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>部屋の色をページへ CSS 変数で渡す（読み込みのたびに、いまのテーマで作り直す）。</summary>
    private async Task ApplyThemeScriptAsync(CoreWebView2 core)
    {
        try
        {
            if (_themeScriptId is { } old)
                core.RemoveScriptToExecuteOnDocumentCreated(old);
            _themeScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(ViewerBridgeScript.ThemeScript(ViewerTheme.Current()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { /* 色が無くてもビューアは動く */ }
    }

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core)
            return;
        CoreWebView2Deferral? deferral = null;
        try
        {
            LocalFileResponse response;
            if (ViewerUrl.TryParse(e.Request.Uri, out var owner, out var relative))
            {
                if (owner != Id || Status != ViewerTabStatus.Running)
                    response = LocalFileResponse.Forbidden();
                else
                {
                    var uri = e.Request.Uri;
                    deferral = e.GetDeferral();
                    response = await Task.Run(() => _store.ResolveFile(Id, relative) is { } path
                        ? LocalFileResponse.Create(path, null, uri)
                        : LocalFileResponse.NotFound());
                }
            }
            else if (Manifest?.Allows(ViewerApi.Net) == true
                     || e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                     || e.Request.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
                     || e.Request.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;   // 通す
            else
                response = LocalFileResponse.Forbidden();
            e.Response = core.Environment.CreateWebResourceResponse(
                response.Content, response.Status, response.Reason, response.Headers);
        }
        catch { /* 応答を作れなければ、ふつうの読み込み失敗として見える */ }
        finally
        {
            try { deferral?.Complete(); } catch { }
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // ビューアのオリジンのページから届いたものだけ聞く（中の iframe や、遷移の隙間の別ページから届いたものは捨てる）。
        if (Manifest is not { } viewer || Status != ViewerTabStatus.Running || !ViewerUrl.IsOf(Id, e.Source))
            return;
        JsonObject? message;
        try { message = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; }
        catch (JsonException) { return; }
        if (message is null || (string?)message["k"] != "call" || message["id"] is not JsonValue idNode
            || !idNode.TryGetValue<int>(out var callId) || (string?)message["m"] is not { Length: > 0 } method)
            return;
        var args = message["p"] as JsonObject;
        var cancel = _pageCancel.Token;
        JsonObject reply;
        try
        {
            // 画面が要る口は相手（IViewerRoom）が Dispatcher を通す。ここは UI スレッドを塞がない。
            var value = await Task.Run(() => _api.CallAsync(viewer, method, args, EmitFromAnyThread, cancel), cancel);
            reply = new JsonObject { ["k"] = "result", ["id"] = callId, ["ok"] = true, ["v"] = value?.DeepClone() };
        }
        catch (ViewerApiException ex)
        {
            reply = new JsonObject { ["k"] = "result", ["id"] = callId, ["ok"] = false, ["e"] = ex.Message };
        }
        catch (OperationCanceledException)
        {
            return;   // ページが替わった・ビューアを閉じた
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            reply = new JsonObject { ["k"] = "result", ["id"] = callId, ["ok"] = false, ["e"] = "Loomo could not do that: " + ex.Message };
        }
        FlushEvents();   // 溜まっている行を先に
        if (!cancel.IsCancellationRequested)
            Post(reply);

        // 途中経過（出力の行）は溜めて、まとめて 1 回で流す。優先度は答えと同じ——答えより後に届くと、ページは
        // 終わったあとに最後の行を受け取ることになる（Promise が先に解決する）。
        void EmitFromAnyThread(string name, JsonNode data)
        {
            if (cancel.IsCancellationRequested)
                return;
            var json = new JsonObject { ["k"] = "event", ["name"] = name, ["data"] = data }.ToJsonString();
            bool schedule;
            lock (_pendingEvents)
            {
                schedule = _pendingEvents.Count == 0;
                _pendingEvents.Add((cancel, json));
            }
            if (schedule)
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, FlushEvents);
        }
    }

    private void FlushEvents()
    {
        (CancellationToken Cancel, string Json)[] batch;
        lock (_pendingEvents)
        {
            batch = _pendingEvents.ToArray();
            _pendingEvents.Clear();
        }
        foreach (var (cancel, json) in batch)
        {
            if (!cancel.IsCancellationRequested)
                PostJson(json);
        }
    }

    private void Post(JsonObject message) => PostJson(message.ToJsonString());

    private void PostJson(string json)
    {
        try { _web.TryCore()?.PostWebMessageAsJson(json); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException) { }
    }

    /// <summary>ビューアのフォルダーを見張り、書き直されたら読み直す（置き場のファイルは除く——自分の書き込みで回り続ける）。</summary>
    private void Watch()
    {
        try
        {
            var folder = _store.FolderOf(Id);
            if (!Directory.Exists(folder))
                return;
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, e) => OnFolderChanged(e.Name);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, e) => OnFolderChanged(e.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void OnFolderChanged(string? name)
    {
        var file = Path.GetFileName(name ?? "");
        if (file.StartsWith(ViewerStore.StateFileName, StringComparison.OrdinalIgnoreCase))
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.IsVisibleChanged -= OnHostVisibleChanged;
        _reloadTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        Unload();
        _pageCancel.Dispose();
        Changed = null;
    }
}

/// <summary>ビューアに渡す部屋の色（テーマの鍵 → CSS の色）。</summary>
internal sealed record ViewerTheme(string Bg, string Panel, string Fg, string FgDim, string Accent, string Border, double FontSize)
{
    public static ViewerTheme Current()
    {
        static string Color(string key, string fallback)
            => Application.Current?.TryFindResource(key) is SolidColorBrush brush
                ? $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"
                : fallback;
        var size = Application.Current?.TryFindResource("Fs13") is double fs ? fs : 13;
        return new ViewerTheme(Color("Bg", "#1E1E1E"), Color("Panel", "#252526"), Color("Fg", "#D4D4D4"),
            Color("FgDim", "#858585"), Color("Accent", "#3794FF"), Color("Border", "#3C3C3C"), size);
    }
}

/// <summary>ビューアのページへ差し込むスクリプト（<c>window.loomo</c>、§44.2）。中身は <c>postMessage</c> の往復だけ。</summary>
internal static class ViewerBridgeScript
{
    public static string ThemeScript(ViewerTheme theme)
    {
        var values = new JsonObject
        {
            ["--loomo-bg"] = theme.Bg,
            ["--loomo-panel"] = theme.Panel,
            ["--loomo-fg"] = theme.Fg,
            ["--loomo-fg-dim"] = theme.FgDim,
            ["--loomo-accent"] = theme.Accent,
            ["--loomo-border"] = theme.Border,
            ["--loomo-font-size"] = theme.FontSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "px",
        };
        // 文書ができた時点では <html> がまだ無い。生えるのを待ってから載せる（最初の描画より前）。
        return "(() => { const v = " + values.ToJsonString() + ";"
            + " const apply = () => { const s = document.documentElement.style; for (const k in v) s.setProperty(k, v[k]); };"
            + " if (document.documentElement) { apply(); return; }"
            + " const o = new MutationObserver(() => { if (document.documentElement) { o.disconnect(); apply(); } });"
            + " o.observe(document, { childList: true }); })();";
    }

    public const string Script = """
        (() => {
          if (window.loomo || !window.chrome || !window.chrome.webview) return;
          let seq = 0;
          const pending = new Map(), handlers = new Map(), lines = new Map();
          const call = (m, p) => new Promise((resolve, reject) => {
            const id = ++seq;
            pending.set(id, { resolve, reject });
            window.chrome.webview.postMessage({ k: 'call', id, m, p: p || {} });
          });
          window.chrome.webview.addEventListener('message', e => {
            const d = e.data;
            if (!d) return;
            if (d.k === 'result') {
              const w = pending.get(d.id);
              if (!w) return;
              pending.delete(d.id);
              if (d.ok) w.resolve(d.v); else w.reject(new Error(d.e));
            } else if (d.k === 'event') {
              if (d.name === 'run.output') {
                const f = d.data && lines.get(d.data.run);
                if (f) { try { f(d.data.line); } catch (x) { console.error(x); } }
                return;
              }
              (handlers.get(d.name) || []).forEach(h => { try { h(d.data); } catch (x) { console.error(x); } });
            }
          });
          window.loomo = Object.freeze({
            workspace: Object.freeze({
              folders: () => call('workspace.folders'),
              activeFile: () => call('workspace.activeFile'),
            }),
            open: (path, line) => call('open', { path, line: line || 0 }),
            openUrl: url => call('openUrl', { url }),
            fs: Object.freeze({
              read: path => call('fs.read', { path }),
              list: path => call('fs.list', { path }),
            }),
            git: Object.freeze({ status: () => call('git.status') }),
            run: (name, options) => {
              const o = options || {};
              const run = 'r' + (++seq);
              if (typeof o.onLine === 'function') lines.set(run, o.onLine);
              return call('run', { name, args: (o.args || []).map(String), stdin: o.stdin || '', run })
                .finally(() => lines.delete(run));
            },
            state: Object.freeze({
              get: () => call('state.get'),
              set: value => call('state.set', { value: value === undefined ? null : value }),
            }),
            toast: text => call('toast', { text: String(text) }),
            motion: Object.freeze({
              state: () => call('motion.state'),
              snapshots: () => call('motion.snapshots'),
              get: id => call('motion.get', { id }),
              image: (id, file) => call('motion.image', { id, file }),
              record: () => call('motion.record'),
              openRequest: id => call('motion.openRequest', { id }),
            }),
            orders: Object.freeze({
              state: () => call('orders.state'),
              save: order => call('orders.save', order),
              remove: id => call('orders.remove', { id }),
              run: id => call('orders.run', { id }),
              take: id => call('orders.take', { id }),
              discard: id => call('orders.discard', { id }),
              openDiff: id => call('orders.openDiff', { id }),
            }),
            on: (name, handler) => {
              if (!handlers.has(name)) handlers.set(name, []);
              handlers.get(name).push(handler);
              return () => handlers.set(name, handlers.get(name).filter(h => h !== handler));
            },
          });
        })();
        """;
}
