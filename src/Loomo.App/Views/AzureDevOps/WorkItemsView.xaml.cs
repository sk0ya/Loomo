using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ActivityBar の Work Items ポップアップの中身。</summary>
public partial class WorkItemsView : UserControl
{
    public WorkItemsView() => InitializeComponent();

    /// <summary>開いたらすぐ打ち込めるように絞り込み欄へ。</summary>
    public void FocusFilter()
    {
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    /// <summary>Esc：文字があれば消す。空なら一覧を閉じる。Enter：残っている最初の行を開く。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WorkItemsViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (vm.FilterText.Length > 0) vm.FilterText = "";
            else vm.IsOpen = false;
        }
        else if (e.Key == Key.Enter && vm.Items.FirstOrDefault(r => r.CanOpen) is { } first)
        {
            e.Handled = true;
            vm.OpenCommand.Execute(first);
        }
    }
}
