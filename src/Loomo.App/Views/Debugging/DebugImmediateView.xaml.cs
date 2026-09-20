using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>イミディエイト（REPL）タブ。DataContext は DebugInspectionViewModel。
/// 履歴追加時に最新行を見せ、Enter で式を評価する。</summary>
public partial class DebugImmediateView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public DebugImmediateView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null) _observed.CollectionChanged -= OnImmediateLogChanged;
        if (DataContext is DebugInspectionViewModel vm)
        {
            _observed = vm.ImmediateLog;
            _observed.CollectionChanged += OnImmediateLogChanged;
        }
    }

    // 履歴に追加されたら最新行を見せる（評価結果が下に積まれるので末尾へ）。
    private void OnImmediateLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ImmediateList.Items.Count > 0)
            ImmediateList.ScrollIntoView(ImmediateList.Items[ImmediateList.Items.Count - 1]);
    }

    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    // 入力欄：Enter で評価（停止中＋入力ありのときだけ実行される）。
    private void OnImmediateKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DebugInspectionViewModel vm
            && vm.SubmitImmediateCommand.CanExecute(null))
        {
            vm.SubmitImmediateCommand.Execute(null);
            e.Handled = true;
        }
    }
}
