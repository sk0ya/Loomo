using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 が受け付けない遷移先を例外で漏らさず通知する。</summary>
internal static class BrowserNavigationCommand
{
    /// <summary>指定方向へ履歴を移動できるときだけ WebView2 に実行させる。</summary>
    public static void NavigateHistory(CoreWebView2? core, bool back)
    {
        if (core is null)
            return;
        if (back)
        {
            if (core.CanGoBack)
                core.GoBack();
        }
        else if (core.CanGoForward)
        {
            core.GoForward();
        }
    }

    /// <summary>読み込み中なら停止し、それ以外なら再読み込みする。</summary>
    public static void ReloadOrStop(CoreWebView2? core, bool isLoading)
    {
        if (core is null)
            return;
        if (isLoading)
            core.Stop();
        else
            core.Reload();
    }

    public static bool TryNavigate(CoreWebView2 core, string address)
    {
        try
        {
            core.Navigate(address);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or COMException)
        {
            ToastService.Error($"このアドレスは開けません: {address}");
            return false;
        }
    }
}
