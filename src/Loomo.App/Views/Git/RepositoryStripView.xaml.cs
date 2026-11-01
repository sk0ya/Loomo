using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>リポジトリの帯（§40.8.3）。DataContext は <see cref="ViewModels.GitRepositoryStripViewModel"/>。</summary>
public partial class RepositoryStripView : UserControl
{
    public static readonly DependencyProperty IsHorizontalProperty = DependencyProperty.Register(
        nameof(IsHorizontal), typeof(bool), typeof(RepositoryStripView),
        new PropertyMetadata(false, (d, _) => ((RepositoryStripView)d).ApplyOrientation()));

    public RepositoryStripView()
    {
        InitializeComponent();
        ApplyOrientation();
    }

    /// <summary>横のタブ（Git ペイン）か、縦の一覧（Git パネル）か。</summary>
    public bool IsHorizontal
    {
        get => (bool)GetValue(IsHorizontalProperty);
        set => SetValue(IsHorizontalProperty, value);
    }

    /// <summary>選んでいる項目の印の線：タブは下、一覧は左。</summary>
    public Thickness SelectedBorder => IsHorizontal ? new Thickness(0, 0, 0, 2) : new Thickness(2, 0, 0, 0);

    private void ApplyOrientation()
    {
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, IsHorizontal ? Orientation.Horizontal : Orientation.Vertical);
        List.ItemsPanel = new ItemsPanelTemplate(factory);
        ScrollViewer.SetHorizontalScrollBarVisibility(List, IsHorizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
    }
}
