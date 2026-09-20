using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>比較基準（作業ツリー／ブランチ／分岐点）の選択 UI。
/// DataContext は <see cref="ViewModels.GitCompareBaseViewModel"/>（Singleton）。</summary>
public partial class GitCompareBaseView : UserControl
{
    public GitCompareBaseView()
    {
        InitializeComponent();
    }
}
