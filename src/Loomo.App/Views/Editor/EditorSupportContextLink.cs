namespace sk0ya.Loomo.App.Views;

/// <summary>
/// EditorSupport（Markdown プレビュー等の WebView2 表示）で、右クリックした位置のリンクの
/// <b>生の href</b>（<c>getAttribute</c> の値）を拾うための仕込み。
/// <para>
/// WebView2 が <c>ContextMenuTarget.LinkUri</c> で渡すのは <c>&lt;base&gt;</c> 起点に解決済みの絶対 URL
/// （<c>https://loomo-preview/...</c>）で、そこからワークスペース内のファイルへ戻すことはできない。
/// リンククリックの振り分け（<c>linkClicked</c> メッセージ）が使うのも生の href なので、
/// 右クリックからの「別ウィンドウで開く」も同じ値を見て同じ宛先を開く。
/// </para>
/// </summary>
internal static class EditorSupportContextLink
{
    /// <summary>ドキュメント生成時に流し込むページ側スクリプト
    /// （<c>AddScriptToExecuteOnDocumentCreatedAsync</c> で登録する）。</summary>
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

    /// <summary>
    /// Chromium 既定の「リンクを新しいウィンドウで開く」を落とす。<c>NewWindowRequested</c> を誰も扱って
    /// いないので、これを押すと部屋の外に管理外のポップアップが出る——同じ場所に Loomo の
    /// 「リンク先を別ウィンドウで開く」（切り離しウィンドウ）を置くので、紛らわしい二重の扉を残さない。
    /// </summary>
    public static void RemoveBuiltInOpenInNewWindow(IList<CoreWebView2ContextMenuItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Name is "openLinkInNewWindow")
                items.RemoveAt(i);
        }
    }

    /// <summary>直前の右クリックで記録した href を読む（リンク上でなければ null）。</summary>
    public static async Task<string?> ReadHrefAsync(CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync("window.__loomoContextLink ?? null");
            if (string.IsNullOrEmpty(json) || json == "null")
                return null;
            return JsonSerializer.Deserialize<string>(json);
        }
        catch { return null; }   // スクリプトが届かないページ（PDF ビューア等）：項目を出さないだけ
    }
}
