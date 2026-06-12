using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>ペグボードペイン（設計書 §23.3）。
/// ロジックは <see cref="ViewModels.PegboardViewModel"/> に集約する。</summary>
public partial class PegboardView : UserControl
{
    public PegboardView()
    {
        InitializeComponent();
    }
}
