using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

public partial class WorkflowView : UserControl
{
    public WorkflowView()
    {
        InitializeComponent();
    }

    /// <summary>「開く」ポップアップで保存済みワークフローを選んだら、読込後にポップアップを閉じる
    /// （StaysOpen=False はポップアップ内クリックでは閉じないため、明示的にトグルを戻す）。</summary>
    private void OnSavedWorkflowClick(object sender, RoutedEventArgs e)
    {
        OpenToggle.IsChecked = false;
    }

    /// <summary>「ステップを追加」パレットで候補を選んだら、追加後にパレットを閉じる。</summary>
    private void OnAddCandidateClick(object sender, RoutedEventArgs e)
    {
        AddPaletteToggle.IsChecked = false;
    }
}
