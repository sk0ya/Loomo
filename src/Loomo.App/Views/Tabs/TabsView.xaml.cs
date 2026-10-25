using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class TabsView : UserControl
{
    public TabsView()
    {
        InitializeComponent();
        // 設定ボタン（⚙）とポップアップを同じ旗へ TwoWay で結んでいるので、何もしないと
        // 自分のボタンでは閉じられない（閉じた直後に開き直す）。理由は PopupReopenGuard に。
        PopupReopenGuard.Track(TabsSettingsPopup);
    }

    private void OnSettingsToggle(object sender, MouseButtonEventArgs e)
        => PopupReopenGuard.SuppressReopen(sender, e, TabsSettingsPopup);

    // ＋ は左クリックで種別メニューを開く（右クリック専用にすると押しても何も起きないボタンになる）。
    private void OnNewTabButtonClick(object sender, RoutedEventArgs e)
    {
        NewTabMenu.DataContext = DataContext;
        NewTabMenu.PlacementTarget = TabsNewButton;
        NewTabMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        NewTabMenu.IsOpen = true;
    }

    /// <summary>問い合わせ中の仮置き（XAML の最初の子もこれを読む）。</summary>
    public const string OtherRoomSearchingHeader = "探しています…";

    // 開くたびに増える番号。閉じて開き直したら、前の問い合わせの結果は捨てる。
    private int _otherRoomProbe;

    // 右クリック「他の Loomo で開く」：開くたびに起動中の部屋へ問い合わせて並べ直す（部屋は増えたり減ったりする）。
    // 子の SubmenuOpened も上がってくるので、自分の分だけ扱う。
    private void OnColorSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not MenuItem { DataContext: TabEntryViewModel tab } menu
            || DataContext is not TabsViewModel vm)
            return;
        TerminalTabColors.FillMenu(menu, tab.ColorKey, key => vm.SetTerminalTabColor(tab, key));
    }

    private async void OnOtherRoomSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not MenuItem { DataContext: TabEntryViewModel tab } menu)
            return;

        var probe = ++_otherRoomProbe;
        if (menu.Items.Count != 1 || menu.Items[0] is not MenuItem { IsEnabled: false } placeholder)
        {
            menu.Items.Clear();
            placeholder = new MenuItem { IsEnabled = false };
            menu.Items.Add(placeholder);
        }
        placeholder.Header = OtherRoomSearchingHeader;

        // プロセスの列挙は同期で重いことがあるので、最初から UI スレッドの外で。
        var instances = OtherRoomOpen.Order(await Task.Run(InstanceRelayClient.ProbeAsync));
        if (probe != _otherRoomProbe || !menu.IsSubmenuOpen)
            return;   // 待っている間に閉じられた・開き直された

        menu.Items.Clear();
        if (instances.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "起動中の他の Loomo はありません", IsEnabled = false });
            return;
        }
        var labels = OtherRoomOpen.Labels(instances);
        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var item = new MenuItem
            {
                Header = labels[i],
                ToolTip = instance.Folders.Count == 0 ? null : string.Join("\n", instance.Folders),
            };
            item.Click += (_, _) => _ = SendToOtherRoomAsync(tab, instance.ProcessId);
            menu.Items.Add(item);
        }
        // 受け手はディスクから読むので、保存していない変更は渡らない。
        if (tab.IsModified)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "未保存の変更は渡りません（保存済みの内容で開きます）", IsEnabled = false });
        }
    }

    // 右クリック「ほかのワークツリーで」（§24.80）：開くたびにいまのワークツリー一覧から作り直す（ワークツリーは増えたり減ったりする）。
    // 相手の版にそのファイルが無ければ、行を残して「無い」と書く（黙って消すと、一覧から外れたのか無いのかが分からない）。
    private async void OnWorktreeSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not MenuItem { DataContext: TabEntryViewModel { FilePath: { } path } tab } menu
            || DataContext is not TabsViewModel vm)
            return;
        var probe = ++_otherRoomProbe;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = OtherRoomSearchingHeader, IsEnabled = false });
        var counterparts = vm.WorktreeCounterpartsFor is { } lookup ? await lookup(path) : [];
        if (probe != _otherRoomProbe || !menu.IsSubmenuOpen)
            return;   // 待っている間に閉じられた・開き直された
        menu.Items.Clear();
        if (counterparts.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "ほかのワークツリーはありません", IsEnabled = false });
            return;
        }
        foreach (var target in counterparts)
        {
            if (!target.Exists)
            {
                menu.Items.Add(new MenuItem { Header = $"{target.Label} — このファイルはありません", IsEnabled = false, ToolTip = target.FilePath });
                continue;
            }
            var row = new MenuItem { Header = target.Label, ToolTip = target.FilePath };
            row.Items.Add(Action("この部屋で開く", WorktreeCounterpartAction.OpenHere));
            row.Items.Add(Action("差分で比べる", WorktreeCounterpartAction.Compare));
            row.Items.Add(Action("その部屋へ移って開く", WorktreeCounterpartAction.OpenInRoom));
            menu.Items.Add(row);

            MenuItem Action(string header, WorktreeCounterpartAction action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => vm.RequestWorktreeCounterpart(tab, target, action);
                return item;
            }
        }
    }

    // 渡す中身はクリックした時点のもの（問い合わせを待つ間にナビゲーションや名前を付けて保存で変わりうる）。
    // 消えたファイルは受け手の窓でしか知らせられないので、こちらで先に確かめる。
    private static async Task SendToOtherRoomAsync(TabEntryViewModel tab, int processId)
    {
        if (OtherRoomOpen.RequestFor(tab.FilePath, tab.BrowserUrl) is not { } request)
            return;
        if (request.Files.FirstOrDefault(f => !File.Exists(f)) is { } missing)
        {
            ToastService.Error($"ファイルが見つかりません: {Path.GetFileName(missing)}");
            return;
        }
        if (!await InstanceRelayClient.OpenAsync(processId, request))
            ToastService.Error("他の Loomo に渡せませんでした（終了したか、応答がありません）");
    }

    // タブ行を中ボタンクリックで閉じる
    private void OnTabMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle
            || sender is not FrameworkElement { DataContext: TabEntryViewModel tab }
            || DataContext is not TabsViewModel vm)
            return;

        e.Handled = true;
        if (vm.CloseTabCommand.CanExecute(tab))
            vm.CloseTabCommand.Execute(tab);
    }
}
