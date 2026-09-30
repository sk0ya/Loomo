using System.Windows.Controls;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Views;

/// <summary>比較基準（作業ツリー／ブランチ／分岐点／ワークツリー／リビジョン）の選択 UI。
/// DataContext は <see cref="ViewModels.GitCompareBaseViewModel"/>（Singleton）。</summary>
public partial class GitCompareBaseView : UserControl
{
    public GitCompareBaseView()
    {
        InitializeComponent();
    }

    /// <summary>リビジョン欄の Enter で確定する（フォーカスを外さずに比較し直せるように）。
    /// Esc は打ちかけを捨てて、いま効いている値へ戻す。</summary>
    private void OnRevisionKeyDown(object sender, KeyEventArgs e)
    {
        var binding = RevisionBox.GetBindingExpression(TextBox.TextProperty);
        if (e.Key == Key.Enter)
        {
            binding?.UpdateSource();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            binding?.UpdateTarget();
            e.Handled = true;
        }
    }
}
