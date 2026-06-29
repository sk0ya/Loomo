using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>構成タブ（起動構成・例外オプション・プロセスへのアタッチ・アダプタ未導入バー）。
/// DataContext は DebugViewModel（ファサード）。</summary>
public partial class DebugConfigView : UserControl
{
    public DebugConfigView() => InitializeComponent();

    // アダプタ未導入バーの「再確認」：導入状況を取り直す。
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DebugViewModel vm) vm.Refresh();
    }

    // プロセス一覧のダブルクリック：その行のプロセスへ即アタッチ（行＝ListBoxItem 上のときだけ）。
    private void OnProcessDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem)
            {
                if (DataContext is DebugViewModel vm && vm.Attach.AttachCommand.CanExecute(null))
                    vm.Attach.AttachCommand.Execute(null);
                return;
            }
        }
    }
}
