using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 記事・ドキュメントのコードブロックを右クリック → 手元へ（§41.8）。
/// <para>以前はブラウザサポートの脇にページのコードを並べ直していたが、同じものがページにもう載っている。要るのは
/// 「このコードを、あそこへ」の1手だけなので、コードの上の右クリックに置く。行き先は調べ始めた場所（§41.12）があれば
/// まずそこ——ファイルなら<b>その下の行へ貼る</b>、ターミナルなら<b>そのタブのプロンプトへ</b>。</para>
/// <para>右クリックした所の <c>pre</c> を自前のスクリプトで読むだけ（ページからの合図は受けない §41.2）。コードを写して使う
/// ページ（<see cref="BrowserSupportPage.ShowsCode"/>）だけ——GitHub のファイルのようにページ全体がコードなら、手元のファイルを
/// 開く方が早い。</para>
/// </summary>
public partial class ShellWindow {
    private static readonly TimeSpan PageCodeMenuBudget = TimeSpan.FromSeconds(1);

    private void AddPageCodeMenuItems(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e) {
        if (BrowserSupportCatalog.Describe(core.Source) is not { ShowsCode: true })
            return;
        // コードの上でありえない右クリックは待たせない（メニューは deferral の間出ない）：画像・動画・音声、リンク、入力欄。
        // 文字の選択はコードの中でもありうるので通す。
        var target = e.ContextMenuTarget;
        if (target.Kind is not (CoreWebView2ContextMenuTargetKind.Page or CoreWebView2ContextMenuTargetKind.SelectedText)
            || target.HasLinkUri || target.IsEditable)
            return;
        var deferral = e.GetDeferral();
        _ = FillPageCodeMenuAsync(tab, core, e, deferral);
    }

    private async Task FillPageCodeMenuAsync(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e, CoreWebView2Deferral deferral) {
        try {
            // 座標は右クリックされたタブの拡大率で割る（前面のタブとは限らない）。
            var zoom = tab.View.ZoomFactor is > 0 and var z ? z : 1.0;
            var script = BrowserPageCode.ExtractAtScript
                .Replace("%X%", ((int)(e.Location.X / zoom)).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("%Y%", ((int)(e.Location.Y / zoom)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var work = core.ExecuteScriptAsync(script);
            if (await Task.WhenAny(work, Task.Delay(PageCodeMenuBudget)) != work)
                return;
            if (BrowserPageCode.Parse(await work) is not [var block])
                return;
            var items = PageCodeMenuItems(tab, core, block);
            for (var i = items.Count - 1; i >= 0; i--)
                e.MenuItems.Insert(0, items[i]);
            if (e.MenuItems.Count > items.Count)
                e.MenuItems.Insert(items.Count, core.Environment.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        finally {
            deferral.Complete();
        }
    }

    /// <summary>行き先の項目。調べ始めた場所への1手を先頭に、項目名に行き先を書く（押す前にどこへ行くか分かる）。</summary>
    private List<CoreWebView2ContextMenuItem> PageCodeMenuItems(BrowserTab tab, CoreWebView2 core, BrowserCodeBlock block) {
        var items = new List<CoreWebView2ContextMenuItem>();
        var origin = SearchOriginOf(tab);
        var lines = block.LineCount == 1 ? "" : $"（{block.LineCount} 行）";
        if (origin is BrowserSearchOrigin.Editor editor)
            items.Add(MenuCommand(core, $"このコードを {Path.GetFileName(editor.FilePath)} の {editor.EndLine + 1} 行目の下へ貼る{lines}",
                () => _ = PasteCodeAtSearchOriginAsync(tab, block)));
        if (block.LooksLikeCommand) {
            if (origin is BrowserSearchOrigin.Terminal terminal)
                items.Add(MenuCommand(core, $"このコマンドをターミナル「{TerminalTabTitle(terminal.TabId) ?? "Terminal"}」へ入れる",
                    () => SendCodeToSearchOriginTerminal(terminal, block)));
            else
                items.Add(MenuCommand(core, "このコマンドをターミナルへ入れる",
                    () => SendRecentCommandToTerminal(BrowserPageCode.AsCommand(block.Text))));
        }
        items.Add(MenuCommand(core, $"このコードをエディタの新しいタブへ{lines}", () => _ = OpenCodeBlockInEditorAsync(block)));
        items.Add(MenuCommand(core, "このコードをコンポーザへ", () => InsertIntoComposer(block.Text)));
        return items;
    }
}
