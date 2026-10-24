using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Http;

namespace sk0ya.Loomo.App.Views;

/// <summary>.http パネル（§24.30）。中身は <see cref="HttpRequestPanelViewModel"/>。</summary>
public partial class HttpRequestView : UserControl
{
    public HttpRequestView() => InitializeComponent();

    // 一覧で選んだ行を Enter で送る（マウスの ▶ だけにしない）。
    private void OnRequestListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not HttpRequestPanelViewModel vm)
            return;
        e.Handled = true;
        if (vm.SendSelectedCommand.CanExecute(null))
            vm.SendSelectedCommand.Execute(null);
    }

    // 本文の URL を Ctrl+クリックでブラウザペインへ（§24.30.1）。選択のドラッグは邪魔しない（離した位置で判定）。
    private void OnResponseBodyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || ResponseBody.SelectionLength > 0
            || DataContext is not HttpRequestPanelViewModel vm || UrlUnder(e.GetPosition(ResponseBody)) is not { } url)
            return;
        e.Handled = true;
        vm.OpenUrl(url);
    }

    // 右クリックの位置が URL なら、ふつうの編集メニューの頭に URL の行き先を足す。
    private void OnResponseBodyContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not HttpRequestPanelViewModel vm || ResponseBody.ContextMenu is not { } menu)
            return;
        menu.Items.Clear();
        if (UrlUnder(Mouse.GetPosition(ResponseBody)) is { } url)
        {
            AddItem(menu, "URL をブラウザで開く（Ctrl+クリック）", () => vm.OpenUrl(url));
            AddItem(menu, "URL を GET として .http に書き足す", () => vm.AppendUrlAsRequest(url), vm.HasSelection);
            AddItem(menu, "URL をコピー", () => { try { Clipboard.SetText(url); } catch { /* 占有中は無視 */ } });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Copy, CommandTarget = ResponseBody });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.SelectAll, CommandTarget = ResponseBody });
    }

    private string? UrlUnder(Point point)
    {
        var index = ResponseBody.GetCharacterIndexFromPoint(point, snapToText: false);
        return index < 0 ? null : HttpResponseLinks.UrlAt(ResponseBody.Text, index);
    }

    private static void AddItem(ContextMenu menu, string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
