using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

public partial class WorkflowView : UserControl
{
    public WorkflowView()
    {
        InitializeComponent();
    }

    /// <summary>「ステップを追加」パレットで候補を選んだら、追加後にパレットを閉じる。</summary>
    private void OnAddCandidateClick(object sender, RoutedEventArgs e)
    {
        AddPaletteToggle.IsChecked = false;
    }
}
