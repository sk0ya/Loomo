using System.Windows.Controls;
using System.Windows.Data;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ブラウザサポートペイン（§41）。ロジックは <see cref="ViewModels.BrowserSupportViewModel"/> と ShellWindow にある。
/// 節は1つの並び（<see cref="BrowserSupportViewModel.Sections"/>）を、道具の上に出すもの（<see cref="BrowserSupportSection.Leads"/>）と
/// 下に出すものの2つの見え方に分けて見せる——並びの正本は1つのまま。</summary>
public partial class BrowserSupportPaneView : UserControl
{
    public BrowserSupportPaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
    }

    private ListCollectionView? _leadSections, _trailSections;

    private void Bind()
    {
        // 前の見え方は並びの変化の通知を受け続けるので、外してから作り直す（VM はペインより長生きする）。
        _leadSections?.DetachFromSourceCollection();
        _trailSections?.DetachFromSourceCollection();
        _leadSections = _trailSections = null;
        if (DataContext is BrowserSupportViewModel vm)
        {
            // 別々の見え方にする（既定の見え方は1つの並びに1つしかなく、絞り込みが両方に効いてしまう）。
            _leadSections = new ListCollectionView(vm.Sections) { Filter = s => ((BrowserSupportSection)s).Leads };
            _trailSections = new ListCollectionView(vm.Sections) { Filter = s => !((BrowserSupportSection)s).Leads };
        }
        LeadSectionsList.ItemsSource = _leadSections;
        TrailSectionsList.ItemsSource = _trailSections;
    }
}
