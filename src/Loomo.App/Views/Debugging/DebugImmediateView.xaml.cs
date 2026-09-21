using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>イミディエイト（REPL）タブ。DataContext は DebugInspectionViewModel。
/// 履歴追加時に最新行を見せ、Enter で式を評価する。</summary>
public partial class DebugImmediateView : UserControl
{
    private readonly DebugImmediateHistoryController _history;

    public DebugImmediateView()
    {
        InitializeComponent();
        _history = new DebugImmediateHistoryController(ImmediateList);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _history.Attach(DataContext as DebugInspectionViewModel);

    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    // 入力欄：Enter で評価（停止中＋入力ありのときだけ実行される）。
    private void OnImmediateKeyDown(object sender, KeyEventArgs e)
        => _history.HandleKeyDown(e, DataContext as DebugInspectionViewModel);
}
