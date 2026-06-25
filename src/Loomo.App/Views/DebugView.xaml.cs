using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>サイドバー「デバッグ」パネル（Phase 1）。出力追加時に末尾へ自動スクロールする。
/// 検査内容は「出力／変数／コールスタック」のタブに分け、停止時は自動で「変数」タブへ切り替える。</summary>
public partial class DebugView : UserControl
{
    // タブのインデックス（XAML の並び順と一致させる）。
    private const int OutputTab = 0;
    private const int VariablesTab = 1;

    private INotifyCollectionChanged? _observed;
    private DebugViewModel? _vm;

    public DebugView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        if (DataContext is DebugViewModel vm)
        {
            _observed = vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    // 停止したら「変数」タブへ、実行を再開／終了したら「出力」タブへ自動で切り替える。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DebugViewModel.IsStopped) && _vm is not null)
            DebugTabs.SelectedIndex = _vm.IsStopped ? VariablesTab : OutputTab;
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
