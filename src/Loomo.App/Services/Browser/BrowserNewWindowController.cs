using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>target=_blank の遷移を新しいブラウザータブへ受け、失敗時は既定動作へ戻す。</summary>
internal sealed class BrowserNewWindowController(
    Func<string, BrowserTab> createTab,
    Func<BrowserTab, Task> ensureRealized,
    Func<Guid, Task> closeTab)
{
    public async Task HandleAsync(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var uri = e.Uri;
        BrowserTab? created = null;
        try
        {
            e.Handled = true;
            created = createTab(uri);
            await ensureRealized(created);
            if (created.View.TryCore() is { } core)
            {
                e.NewWindow = core;
                return;
            }

            // 初回の実体化に失敗したときだけ、自分で再試行する。
            created.PendingUrl = uri;
            await ensureRealized(created);
            if (created.View.TryCore() is not null)
                return;

            e.Handled = false;
            await closeTab(created.Id);
        }
        catch
        {
            e.Handled = false;
            if (created is not null)
                await closeTab(created.Id);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
