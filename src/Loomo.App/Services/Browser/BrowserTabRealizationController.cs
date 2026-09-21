using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザタブのWebView2実体化とプロセス障害からの再生成を扱う。</summary>
internal sealed class BrowserTabRealizationController(
    Dispatcher dispatcher,
    Func<CoreWebView2CreationProperties> creationProperties,
    Func<BrowserTab, Task> rebuildView)
{
    private const int MaxRendererReloads = 2;

    /// <summary>表示される時点まで遅らせたCoreWebView2生成を一度だけ開始する。</summary>
    public async Task<CoreWebView2?> EnsureCoreAsync(BrowserTab tab)
    {
        if (tab.RealizationStarted)
            return null;
        tab.RealizationStarted = true;
        // タブ作成後にWebView2の共有プロファイル引数が引き直される場合に備え、実体化直前に設定する。
        tab.View.CreationProperties = creationProperties();
        try
        {
            await tab.View.EnsureCoreWebView2Async();
        }
        catch
        {
            tab.RealizationStarted = false;
            if (WebViewEnvironment.TryRecover())
                await rebuildView(tab);
            else
                WebViewEnvironment.ReportUnavailable("ブラウザ");
            return null;
        }

        WebViewEnvironment.NoteCreated();
        return tab.View.TryCore();
    }

    /// <summary>描画プロセスは回数を区切って再読込し、ブラウザプロセスの終了は次のDispatcherで器ごと作り直す。</summary>
    public void HandleProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            if (tab.RendererReloads++ < MaxRendererReloads)
                try { tab.View.TryCore()?.Reload(); } catch { }
            return;
        }

        // イベント配信中にコントロールを破棄しない。
        dispatcher.BeginInvoke(new Action(() => _ = rebuildView(tab)));
    }
}
