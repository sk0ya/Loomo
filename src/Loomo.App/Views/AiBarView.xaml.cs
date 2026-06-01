using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

public partial class AiBarView : UserControl
{
    public AiBarView() => InitializeComponent();

    /// <summary>AI 入力欄へキーボードフォーカスを移す（ペイン間ナビゲーション用）。</summary>
    public void FocusInput() => InputBox.Focus();
}
