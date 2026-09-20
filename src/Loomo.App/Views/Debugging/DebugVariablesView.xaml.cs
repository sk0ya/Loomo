using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>変数（＋ウォッチ）タブ。DataContext は DebugInspectionViewModel。
/// 値のインライン編集（setVariable 対応アダプタ）とウォッチ式の追加を扱う。</summary>
public partial class DebugVariablesView : UserControl
{
    public DebugVariablesView() => InitializeComponent();

    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    // 変数の右クリック「値を変更…」：インライン編集を開始する。
    private void OnEditVariableClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DebugVariableViewModel v) v.BeginEdit();
    }

    // 変数のダブルクリック：葉（展開不可）のときだけインライン編集を開始する。展開可能ノードは展開に任せる。
    private void OnVariableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem item)
            {
                if (item.DataContext is DebugVariableViewModel { CanEdit: true, HasChildren: false } v)
                {
                    v.BeginEdit();
                    e.Handled = true;
                }
                return;
            }
        }
    }

    // 編集 TextBox が出たら即フォーカスして全選択（すぐ打ち替えられるように）。
    private void OnVariableEditBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    // 編集 TextBox：Enter で確定（setVariable）、Esc で取消。
    private void OnVariableEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DebugVariableViewModel v }) return;
        if (e.Key == Key.Enter)
        {
            _ = v.CommitEditAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            v.CancelEdit();
            e.Handled = true;
        }
    }

    // 編集 TextBox からフォーカスが外れたら確定する（Esc 後は IsEditing=false なので no-op）。
    private void OnVariableEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DebugVariableViewModel v }) _ = v.CommitEditAsync();
    }

    private void OnWatchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DebugInspectionViewModel vm
            && vm.AddWatchCommand.CanExecute(null))
        {
            vm.AddWatchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
