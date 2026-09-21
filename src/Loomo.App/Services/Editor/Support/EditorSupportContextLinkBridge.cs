using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport ページの右クリックリンクから生の href を読み取るためのページ側 bridge。</summary>
internal static class EditorSupportContextLinkBridge
{
    public const string Script = """
        (() => {
            addEventListener('contextmenu', e => {
                const t = e.target;
                const a = t && t.closest ? t.closest('a[href]') : null;
                const href = a ? a.getAttribute('href') : null;
                // ページ内アンカー（#見出し）はページ内スクロール専用なので拾わない。
                window.__loomoContextLink = href && !href.startsWith('#') ? href : null;
            }, true);
        })();
        """;

    /// <summary>Chromiumの管理外ポップアップ用メニュー項目を取り除く。</summary>
    public static void RemoveBuiltInOpenInNewWindow(IList<CoreWebView2ContextMenuItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Name is "openLinkInNewWindow")
                items.RemoveAt(i);
    }

    /// <summary>直前の右クリックで記録したhrefを読む（リンク上でなければnull）。</summary>
    public static async Task<string?> ReadHrefAsync(CoreWebView2 core)
    {
        try { return ParseHref(await core.ExecuteScriptAsync("window.__loomoContextLink ?? null")); }
        catch { return null; }
    }

    /// <summary>ExecuteScriptAsync が返す JSON 文字列を href へ戻す。</summary>
    public static string? ParseHref(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return null;
        try { return JsonSerializer.Deserialize<string>(json); }
        catch { return null; }
    }
}
