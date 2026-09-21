using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>取り込んだ Cookie を WebView2 の正規 API でプロファイルへ適用する。</summary>
internal static class BrowserCookieImporter
{
    public static int Apply(CoreWebView2CookieManager manager, IReadOnlyList<ImportedCookie> cookies)
    {
        var applied = 0;
        foreach (var item in cookies)
        {
            try
            {
                var cookie = manager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                cookie.IsSecure = item.IsSecure;
                cookie.IsHttpOnly = item.IsHttpOnly;
                cookie.SameSite = item.SameSite switch
                {
                    0 => CoreWebView2CookieSameSiteKind.None,
                    2 => CoreWebView2CookieSameSiteKind.Strict,
                    _ => CoreWebView2CookieSameSiteKind.Lax,
                };
                if (item.ExpiresUtc is { } expires)
                    cookie.Expires = expires;
                manager.AddOrUpdateCookie(cookie);
                applied++;
            }
            catch (Exception ex) when (ex is ArgumentException or COMException)
            {
                // 期限切れや壊れた値の1件で、残りの Cookie を取り込めなくしない。
            }
        }
        return applied;
    }
}
