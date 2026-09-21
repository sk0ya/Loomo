using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>別窓に置いたブラウザーの生成・障害復旧・器の差し替えを管理する。</summary>
internal sealed class DetachedBrowserLifecycleController(
    Dispatcher dispatcher,
    Func<WebView2CompositionControl> createView,
    Func<DetachedItem, bool> isManaged,
    Action<string> openDetachedUrl,
    string defaultBrowserUrl)
{
    private const int MaxRendererReloads = 2;

    public async Task RealizeAsync(
        Panel host, WebView2CompositionControl view, SpinoffBrowserAddress address, DetachedItem item)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
        }
        catch
        {
            // 生成中に器から外れた古い実体の失敗は、作り直し側が処理する。
            if (!IsLive(host, view))
                return;
            if (WebViewEnvironment.TryRecover())
                Rebuild(host, item, address);
            else
                WebViewEnvironment.ReportUnavailable("ブラウザ");
            return;
        }

        if (!IsLive(host, view))
        {
            try { view.Dispose(); } catch { }
            return;
        }

        WebViewEnvironment.NoteCreated();
        if (view.TryCore() is not { } core)
            return;
        BrowserWebViewDefaults.Configure(core);
        var rendererReloads = 0;
        view.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
                return;
            rendererReloads = 0;
            address.Note(view.TryUrl());
        };
        core.SourceChanged += (_, _) => address.Note(view.TryUrl());
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                if (rendererReloads++ < MaxRendererReloads)
                    try { view.TryCore()?.Reload(); } catch { }
                return;
            }

            address.Note(view.TryUrl());
            dispatcher.BeginInvoke(new Action(() => Rebuild(host, item, address)));
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var uri = e.Uri;
            dispatcher.BeginInvoke(new Action(() => openDetachedUrl(uri)));
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var title = view.TryCore()?.DocumentTitle;
            item.Title = string.IsNullOrWhiteSpace(title) ? "Browser" : title!;
        };
        try { view.Source = new Uri(DetachedLaunchTargetPolicy.NormalizeBrowserAddress(address.Value, defaultBrowserUrl)); }
        catch { /* 不正 URL は無視（空ページのまま） */ }
    }

    public void Rebuild(Panel host, DetachedItem item, SpinoffBrowserAddress address)
    {
        // Dispatcher 経由の遅延通知が、すでに別の窓へ移した器を復活させないよう確認する。
        if (!isManaged(item))
            return;
        address.Note(CurrentUrl(host));
        DisposeContent(host);
        host.Children.Clear();
        var view = createView();
        view.Visibility = Visibility.Visible;
        host.Children.Add(view);
        _ = RealizeAsync(host, view, address, item);
    }

    /// <summary>作り直し後も戻し先を保てるよう、ホストが表示している URL を取り出す。</summary>
    public static string? CurrentUrl(Panel host)
        => host.Children.OfType<WebView2CompositionControl>().FirstOrDefault().TryUrl();

    /// <summary>器から WebView2 を外して破棄する。</summary>
    public static void DisposeContent(Panel host)
    {
        foreach (var view in host.Children.OfType<WebView2CompositionControl>().ToList())
        {
            host.Children.Remove(view);
            try { view.Dispose(); } catch { }
        }
    }

    private static bool IsLive(Panel host, WebView2CompositionControl view)
        => host.Children.Contains(view);
}
