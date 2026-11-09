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

    /// <summary>ペインへフォーカスを移したとき（キーボードで送れるように、見えているタブの中身へ落とす）。</summary>
    public void FocusList()
    {
        if (RunList.IsVisible)
            RunList.Focus();
        else if (ConfigList.IsVisible)
            ConfigList.Focus();
        else if (AnalysisPromptBox.IsVisible)
            AnalysisPromptBox.Focus();
        else
            Focus();
    }

    private void OnConfigDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HarnessPaneViewModel vm && vm.SelectedConfig is { } row)
            vm.OpenConfigCommand.Execute(row);
    }

    private void OnConfigKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is HarnessPaneViewModel vm && vm.SelectedConfig is { } row)
        {
            vm.OpenConfigCommand.Execute(row);
            e.Handled = true;
        }
    }
}
