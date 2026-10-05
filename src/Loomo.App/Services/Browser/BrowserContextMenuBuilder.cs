namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 の既定コンテキストメニューへ Loomo の操作を組み立てる。
/// Loomo の操作は<b>先頭の「Loomo」サブメニュー1つにすべて入れ</b>、その下に区切り線を挟んで Chromium 既定の項目を残す。
/// サブメニューの中は「選択 → リンク → このページ」の順に区切り線で分ける（右クリックした対象にあるものだけ）。
/// 動詞は §24.3 の共通語彙（送る／開く／残す）に揃える——ペグボードへ入れるのは「残す」で、「ピン」は使わない。</summary>
internal static class BrowserContextMenuBuilder
{
    public static void AddItems(
        CoreWebView2 core,
        CoreWebView2ContextMenuRequestedEventArgs e,
        Dispatcher dispatcher,
        Action<string> askAbout,
        Action<string, string, string?> pinText,
        Action<string> sendToComposer,
        Action<string, string?> pinPage,
        Action<string> openNewTab,
        Action<string> openDetachedWindow,
        Action<string, string?> pinLink,
        Action sendPageToEditor,
        bool isBookmarked,
        string bookmarkBarMenuText,
        Action toggleBookmark,
        Action toggleBookmarkBar,
        Action openFind,
        Action<string> copyUrl,
        Action<string> openExternalBrowser)
    {
        try
        {
            var target = e.ContextMenuTarget;
            var loomo = core.Environment.CreateContextMenuItem(
                "Loomo", null, CoreWebView2ContextMenuItemKind.Submenu);
            var children = loomo.Children;
            // ページの URL と題名はメニューを開いた時点のもの（選んでいる間に遷移しても、対象は開いたときのページ）。
            var url = core.Source;
            var title = core.DocumentTitle;

            if (target.HasSelection && !string.IsNullOrWhiteSpace(target.SelectionText))
            {
                var selection = target.SelectionText;
                children.Add(Command(core, dispatcher, "選択をAIへ送る", () => askAbout(selection)));
                children.Add(Command(core, dispatcher, "選択をコンポーザへ送る", () => sendToComposer(selection)));
                children.Add(Command(core, dispatcher, "選択をペグボードへ残す（出典付き）",
                    () => pinText(selection, url, title)));
                children.Add(Separator(core));
            }
            if (target.HasLinkUri && !string.IsNullOrWhiteSpace(target.LinkUri))
            {
                var link = target.LinkUri;
                var linkText = string.IsNullOrWhiteSpace(target.LinkText) ? null : target.LinkText;
                children.Add(Command(core, dispatcher, "リンクを新しいタブで開く", () => openNewTab(link)));
                children.Add(Command(core, dispatcher, "リンクを別ウィンドウで開く", () => openDetachedWindow(link)));
                children.Add(Command(core, dispatcher, "リンクをペグボードへ残す", () => pinLink(link, linkText)));
                children.Add(Separator(core));
            }
            children.Add(Command(core, dispatcher, "このページをエディタへ送る（Markdown）", sendPageToEditor));
            children.Add(Command(core, dispatcher, "このページをペグボードへ残す", () => pinPage(url, title)));
            children.Add(Command(core, dispatcher,
                isBookmarked ? "ブックマークを外す" : "ブックマークに追加", toggleBookmark));
            children.Add(Command(core, dispatcher, bookmarkBarMenuText, toggleBookmarkBar));
            children.Add(Separator(core));
            children.Add(Command(core, dispatcher, "ページ内を検索…", openFind));
            children.Add(Command(core, dispatcher, "URL をコピー", () => copyUrl(url)));
            children.Add(Command(core, dispatcher, "外部ブラウザで開く", () => openExternalBrowser(url)));

            e.MenuItems.Insert(0, loomo);
            // Loomo と Chromium 既定の項目（コピー・印刷…）の境目。
            if (e.MenuItems.Count > 1 && e.MenuItems[1].Kind != CoreWebView2ContextMenuItemKind.Separator)
                e.MenuItems.Insert(1, Separator(core));
        }
        catch
        {
            // メニューを組めなくても WebView2 の既定メニューは出す。
        }
    }

    private static CoreWebView2ContextMenuItem Command(
        CoreWebView2 core, Dispatcher dispatcher, string label, Action action)
    {
        var item = core.Environment.CreateContextMenuItem(
            label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => dispatcher.BeginInvoke(action);
        return item;
    }

    private static CoreWebView2ContextMenuItem Separator(CoreWebView2 core)
        => core.Environment.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator);
}
