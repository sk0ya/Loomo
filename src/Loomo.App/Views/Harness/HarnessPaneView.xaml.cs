using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ハーネスペイン（§37）。ロジックは <see cref="HarnessPaneViewModel"/> に集約し、ここはダブルクリックの受け口だけ。
/// </summary>
public partial class HarnessPaneView : UserControl
{
    public HarnessPaneView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && DataContext is HarnessPaneViewModel vm)
                vm.EnsureLoaded();
        };
    }

    /// <summary>ペインへフォーカスを移したとき（キーボードで候補を送れるように一覧へ落とす）。</summary>
    public void FocusList()
    {
        if (CandidateList.IsVisible)
            CandidateList.Focus();
        else
            Focus();
    }

    private void OnEvidenceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HarnessPaneViewModel vm && vm.SelectedEvidence is { } row)
            vm.OpenEvidenceRecordCommand.Execute(row);
    }

    private void OnConfigDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HarnessPaneViewModel vm && vm.SelectedConfig is { } row)
            vm.OpenConfigCommand.Execute(row);
    }
}
