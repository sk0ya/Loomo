using System.Collections.Specialized;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>サイドバー「デバッグ」パネル（Phase 1）。出力追加時に末尾へ自動スクロールする。</summary>
public partial class DebugView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public DebugView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
        if (DataContext is DebugViewModel vm)
        {
            _observed = vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
        }
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ConsoleScroll.ScrollToEnd();
    }

    private void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DebugViewModel vm) vm.Refresh();
    }

    private void OnWatchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && DataContext is DebugViewModel vm
            && vm.AddWatchCommand.CanExecute(null))
        {
            vm.AddWatchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
