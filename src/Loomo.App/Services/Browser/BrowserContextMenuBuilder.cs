namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 の既定コンテキストメニューへ Loomo のページ操作を組み立てる。</summary>
internal static class BrowserContextMenuBuilder
{
    public static void AddItems(
        CoreWebView2 core,
        CoreWebView2ContextMenuRequestedEventArgs e,
        Dispatcher dispatcher,
        Action<string> askAbout,
        Action<string> pinText,
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
            var items = new List<CoreWebView2ContextMenuItem>();
            if (target.HasSelection && !string.IsNullOrWhiteSpace(target.SelectionText))
            {
                var selection = target.SelectionText;
                items.Add(Command(core, dispatcher, "AIへ送る", () => askAbout(selection)));
                items.Add(Command(core, dispatcher, "ペグボードへ送る", () => pinText(selection)));
            }
            if (target.HasLinkUri && !string.IsNullOrWhiteSpace(target.LinkUri))
            {
                var link = target.LinkUri;
                var linkText = string.IsNullOrWhiteSpace(target.LinkText) ? null : target.LinkText;
                items.Add(Command(core, dispatcher, "リンクを新しいタブで開く", () => openNewTab(link)));
                items.Add(Command(core, dispatcher, "リンクを別ウィンドウで開く", () => openDetachedWindow(link)));
                items.Add(Command(core, dispatcher, "リンクをペグボードへピン", () => pinLink(link, linkText)));
            }
            if (items.Count > 0)
                items.Add(Separator(core));
            items.Add(BuildPageMenu(
                core, dispatcher, pinPage, sendPageToEditor, isBookmarked, bookmarkBarMenuText,
                toggleBookmark, toggleBookmarkBar, openFind, copyUrl, openExternalBrowser));
            for (var i = 0; i < items.Count; i++)
                e.MenuItems.Insert(i, items[i]);
        }
        catch
        {
            // メニューを組めなくても WebView2 の既定メニューは出す。
        }
    }

    private static CoreWebView2ContextMenuItem BuildPageMenu(
        CoreWebView2 core,
        Dispatcher dispatcher,
        Action<string, string?> pinPage,
        Action sendPageToEditor,
        bool isBookmarked,
        string bookmarkBarMenuText,
        Action toggleBookmark,
        Action toggleBookmarkBar,
        Action openFind,
        Action<string> copyUrl,
        Action<string> openExternalBrowser)
    {
        var parent = core.Environment.CreateContextMenuItem(
            "Loomo", null, CoreWebView2ContextMenuItemKind.Submenu);
        var url = core.Source;
        var title = core.DocumentTitle;
        parent.Children.Add(Command(core, dispatcher, "このページをエディタへ送る（Markdown）", sendPageToEditor));
        parent.Children.Add(Command(core, dispatcher, "このページをペグボードへピン",
            () => pinPage(url, title)));
        parent.Children.Add(Command(core, dispatcher,
            isBookmarked ? "ブックマークを外す" : "ブックマークに追加", toggleBookmark));
        parent.Children.Add(Command(core, dispatcher, bookmarkBarMenuText, toggleBookmarkBar));
        parent.Children.Add(Separator(core));
        parent.Children.Add(Command(core, dispatcher, "ページ内を検索…", openFind));
        parent.Children.Add(Command(core, dispatcher, "URL をコピー", () => copyUrl(url)));
        parent.Children.Add(Command(core, dispatcher, "外部ブラウザで開く", () => openExternalBrowser(url)));
        return parent;
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
