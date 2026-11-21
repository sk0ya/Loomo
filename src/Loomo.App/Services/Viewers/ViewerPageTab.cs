using System.Security.Cryptography;
using System.Text;
using sk0ya.Loomo.Core.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// パスから開いた HTML の頁（§44.2「パスから開く」）。頁のあるフォルダーを、そのフォルダーだけのオリジン
/// （<c>https://&lt;パスの指紋&gt;.page.loomo/</c>）で答え、<b>部屋の API（<c>window.loomo</c>）は差し込まない</b>。
/// <para><b>ネットには出さない</b>：頁はそのフォルダー（と下）のファイルを読めるので、外へ出られると隣のファイル（ダウンロード
/// フォルダーの別の書類・リポジトリの <c>.git/config</c> など）を読んで送れてしまう。自分のオリジンと <c>data:</c>・<c>blob:</c> 以外の
/// 要求は断り、WebSocket・WebRTC は頁に渡さず、中の iframe も外へは行かせない。頁の外への遷移はさせず、ユーザーが押した http(s)
/// のリンクはブラウザペインへ。フォルダーの中が書き換わったら読み直す。</para>
/// </summary>
internal sealed class ViewerPageTab : ViewerTabBase
{
    private const string HostSuffix = ".page.loomo";
    private readonly string _folder;
    private readonly string _page;
    private readonly string _hostName;
    private readonly IEditorSupportViewFactory _factory;
    private readonly Func<string, Task> _openUrl;
    private readonly DispatcherTimer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private WebView2CompositionControl? _web;
    private Task<bool>? _init;
    private string? _error;

    public ViewerPageTab(string targetPath, string page, IEditorSupportViewFactory factory, Panel host,
        Dispatcher dispatcher, Func<string, Task> openUrl)
        : base(targetPath, host, dispatcher)
    {
        _page = page;
        _folder = Path.GetDirectoryName(page) ?? targetPath;
        _hostName = HostFor(_folder);
        _factory = factory;
        _openUrl = openUrl;
        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };
        Watch();
    }

    /// <summary>フォルダーごとのホスト名（ほかの頁や Viewer とオリジンを分ける）。</summary>
    internal static string HostFor(string folder)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
        return "p" + Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + HostSuffix;
    }

    /// <summary>頁の URL（フォルダーからの相対パスを区切りごとにエスケープ）。</summary>
    internal static string UrlFor(string hostName, string folder, string page)
    {
        var relative = Path.GetRelativePath(folder, page).Replace('\\', '/');
        return $"https://{hostName}/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    public override string ToolTip => TargetPath + "\nHTML の頁として表示（部屋の API なし・ネットには出ない）";

    public override UIElement? BuildCard()
    {
        if (Status != ViewerTabStatus.Broken)
            return null;
        var panel = ViewerCards.Panel();
        panel.Children.Add(ViewerCards.Line($"「{Title}」を出せません", "Fs14", "Fg", bold: true));
        panel.Children.Add(ViewerCards.Mono(_error ?? ""));
        panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button("もう一度", "SecondaryButton", () =>
        {
            Unmount();
            SetStatus(ViewerTabStatus.Pending);
            OnShownChanged();
        })));
        return panel;
    }

    public override Task RefreshAsync(bool reload = false)
    {
        if (reload)
            Reload();
        else
            OnShownChanged();
        return Task.CompletedTask;
    }

    public override void Focus() => _web?.Focus();

    protected override void OnShownChanged()
    {
        if (!CanShow)
        {
            if (_web is not null)
                _web.Visibility = Visibility.Collapsed;
            return;
        }
        if (Status == ViewerTabStatus.Broken)
            return;
        if (_web is null)
            _ = LoadAsync();
        else
            _web.Visibility = Visibility.Visible;
    }

    private async Task LoadAsync()
    {
        if (_web is not null)
            return;
        var web = _web = _factory.Create();
        Host.Children.Add(web);
        _init = InitAsync(web);
        if (!await _init || !ReferenceEquals(_web, web))
        {
            if (ReferenceEquals(_web, web))
            {
                _error ??= "WebView2 could not be started.";
                Unmount();
                SetStatus(ViewerTabStatus.Broken);
            }
            return;
        }
        web.Visibility = CanShow ? Visibility.Visible : Visibility.Collapsed;
        try { web.TryCore()?.Navigate(UrlFor(_hostName, _folder, _page)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException) { _error = ex.Message; }
        SetStatus(ViewerTabStatus.Running);
    }

    private async Task<bool> InitAsync(WebView2CompositionControl web)
    {
        if (!await _factory.InitializeAsync(web) || web.TryCore() is not { } core)
            return false;
        try
        {
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(_hostName, _folder, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            _error = ex.Message;
            return false;
        }
        core.NavigationStarting += (_, e) =>
        {
            if (IsLocal(e.Uri))
                return;
            e.Cancel = true;   // 頁の外へは出ない
            if (e.IsUserInitiated && IsWebUrl(e.Uri))
                _ = _openUrl(e.Uri);
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!IsLocal(e.Uri))
                e.Cancel = true;   // 中の iframe も外へは行かせない
        };
        // 外向きの要求は全部断る（自分のオリジンはフォルダーの割り当てが答える）。
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (IsLocal(e.Request.Uri))
                return;
            try { e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException) { }
        };
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(NoSocketsScript); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated && IsWebUrl(e.Uri))
                _ = _openUrl(e.Uri);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                Unmount();
                OnShownChanged();   // 落ちたのは Chromium 側。同じ頁を組み直す
            });
        };
        return true;
    }

    /// <summary>WebSocket・WebRTC は要求の見張り（WebResourceRequested）を通らないので、頁から使えなくする。</summary>
    private const string NoSocketsScript =
        "(() => { for (const k of ['WebSocket', 'WebTransport', 'RTCPeerConnection', 'webkitRTCPeerConnection', 'EventSource']) {"
        + " try { Object.defineProperty(window, k, { value: undefined, configurable: false, writable: false }); } catch (e) {} } })();";

    private bool IsOwnPage(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
           && parsed.Scheme == Uri.UriSchemeHttps
           && string.Equals(parsed.Host, _hostName, StringComparison.OrdinalIgnoreCase);

    private bool IsLocal(string uri)
        => IsOwnPage(uri)
           || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
           || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
           || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebUrl(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
           && !parsed.Host.EndsWith(".loomo", StringComparison.OrdinalIgnoreCase);

    private void Reload()
    {
        if (Disposed || _web.TryCore() is not { } core)
            return;
        try { core.Reload(); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
    }

    /// <summary>頁のフォルダーを見張り、書き直されたら読み直す（.git の中は見ない）。</summary>
    private void Watch()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return;
            _watcher = new FileSystemWatcher(_folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
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
        var relative = (name ?? "").Replace('/', '\\');
        if (relative.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase) || relative.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("node_modules\\", StringComparison.OrdinalIgnoreCase))
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Disposed)
                return;
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    protected override void Unmount()
    {
        if (_web is null)
            return;
        Host.Children.Remove(_web);
        _factory.Dispose(_web);
        _web = null;
        _init = null;
    }

    public override void Dispose()
    {
        _reloadTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        base.Dispose();
    }
}
