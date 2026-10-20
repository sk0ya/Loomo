using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ページ遷移完了時に、ブラウザタブと表示状態を同期する。</summary>
internal static class BrowserNavigationCompletionController
{
    internal static void Apply(
        BrowserTab tab,
        bool isActive,
        bool addressHasFocus,
        bool succeeded,
        string? url,
        string? title,
        BrowserViewModel browser,
        Action<BrowserTab> updateTab,
        Action<BrowserTab> updateToolbar,
        Action<BrowserTab> refreshIcon,
        Action<string> setAddress,
        Action<string?, string?, string?> recordTrail,
        Action<BrowserTab> evaluateExtensionPrompt,
        DateTime nowUtc)
    {
        tab.IsLoading = false;
        // 中継ページからの遷移かどうかは、この遷移の間だけの話。
        var redirectFrom = tab.RedirectFromUrl;
        tab.RedirectFromUrl = null;
        if (succeeded)
        {
            tab.LastCompletedUtc = nowUtc;
            tab.LastCompletedUrl = url;
        }
        updateTab(tab);
        updateToolbar(tab);
        refreshIcon(tab);
        if (!isActive)
            return;

        if (!addressHasFocus)
            setAddress(url ?? string.Empty);
        if (succeeded)
        {
            tab.RendererReloads = 0;
            recordTrail(url, title, redirectFrom);
            browser.RecordVisit(url, title);
        }
        evaluateExtensionPrompt(tab);
    }
}
