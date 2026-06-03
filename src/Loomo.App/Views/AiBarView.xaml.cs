using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class AiBarView : UserControl
{
    public AiBarView() => InitializeComponent();

    /// <summary>AI 入力欄へキーボードフォーカスを移す（ペイン間ナビゲーション用）。</summary>
    public void FocusInput() => InputBox.Focus();

    /// <summary>コマンド補完ポップアップが開いているときの上下/確定/取消キーを処理する。</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not AiBarViewModel vm || !vm.IsCommandPopupOpen) return;

        switch (e.Key)
        {
            case Key.Down: vm.MoveCommandSelection(1); e.Handled = true; break;
            case Key.Up: vm.MoveCommandSelection(-1); e.Handled = true; break;
            // 候補が確定できないときは Tab/Enter を素通しし、本来の挙動（フォーカス移動・通常送信）に委ねる。
            case Key.Tab: e.Handled = vm.CompleteSelectedCommand(); break;
            case Key.Enter: e.Handled = vm.AcceptAndRunSelectedCommand(); break;
            case Key.Escape: vm.CloseCommandPopup(); e.Handled = true; break;
        }
    }

    /// <summary>補完候補をクリックで確定・実行する。</summary>
    private void OnCommandListClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AiBarViewModel vm)
            vm.AcceptAndRunSelectedCommand();
    }
}
