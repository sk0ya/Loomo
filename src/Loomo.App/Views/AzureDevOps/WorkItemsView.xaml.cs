using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ActivityBar の Work Items ポップアップの中身。</summary>
public partial class WorkItemsView : UserControl
{
    public WorkItemsView() => InitializeComponent();

    private void OnSavePatClick(object sender, RoutedEventArgs e) => SavePat();

    private void OnPatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SavePat();
    }

    /// <summary>PasswordBox は値をバインドできないので、ここで取り出して渡し、すぐ消す。</summary>
    private void SavePat()
    {
        if (DataContext is not WorkItemsViewModel vm) return;
        var pat = PatBox.Password;
        PatBox.Clear();
        vm.SavePatCommand.Execute(pat);
    }
}
