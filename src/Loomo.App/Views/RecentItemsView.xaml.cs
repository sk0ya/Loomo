using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

public partial class RecentItemsView
{
    public RecentItemsView() => InitializeComponent();

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is RecentItemsViewModel vm
            && sender is FrameworkElement element
            && element.DataContext is RecentUsageItem item)
            vm.Navigate(item);
    }
}
