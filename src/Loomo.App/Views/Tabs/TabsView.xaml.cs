using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
