using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>変数（＋ウォッチ）タブ。DataContext は DebugInspectionViewModel。
/// 値のインライン編集（setVariable 対応アダプタ）とウォッチ式の追加を扱う。</summary>
public partial class DebugVariablesView : UserControl
{
    private readonly DebugVariablesInteractionController _interaction;

    public DebugVariablesView()
    {
        InitializeComponent();
        _interaction = new DebugVariablesInteractionController(() => DataContext as DebugInspectionViewModel);
    }

    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    // 変数の右クリック「値を変更…」：インライン編集を開始する。
    private void OnEditVariableClick(object sender, RoutedEventArgs e) => _interaction.BeginEdit(sender);

    // 変数のダブルクリック：葉（展開不可）のときだけインライン編集を開始する。展開可能ノードは展開に任せる。
    private void OnVariableDoubleClick(object sender, MouseButtonEventArgs e) => _interaction.BeginLeafEdit(e);

    // 編集 TextBox が出たら即フォーカスして全選択（すぐ打ち替えられるように）。
    private void OnVariableEditBoxLoaded(object sender, RoutedEventArgs e)
        => DebugVariablesInteractionController.FocusEditor(sender);

    // 編集 TextBox：Enter で確定（setVariable）、Esc で取消。
    private void OnVariableEditKeyDown(object sender, KeyEventArgs e)
        => DebugVariablesInteractionController.HandleEditKeyDown(sender, e);

    // 編集 TextBox からフォーカスが外れたら確定する（Esc 後は IsEditing=false なので no-op）。
    private void OnVariableEditLostFocus(object sender, RoutedEventArgs e)
        => DebugVariablesInteractionController.CommitOnLostFocus(sender);

    private void OnWatchKeyDown(object sender, KeyEventArgs e) => _interaction.HandleWatchKeyDown(e);
}
