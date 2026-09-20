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
