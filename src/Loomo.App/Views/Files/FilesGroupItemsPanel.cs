using System.Windows;
using System.Windows.Controls;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>グループ内の項目パネル。詳細・一覧は縦並び、アイコン系は折り返しを維持する。</summary>
public sealed class FilesGroupItemsPanel : Panel
{
    private FilesColumnViewModel? _owner;

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureOwner();
        foreach (UIElement child in InternalChildren)
            child.Measure(IsIconMode
                ? new Size(double.PositiveInfinity, double.PositiveInfinity)
                : new Size(availableSize.Width, double.PositiveInfinity));

        var sizes = InternalChildren.Cast<UIElement>().Select(child => child.DesiredSize).ToArray();
        return IsIconMode
            ? FilesGroupItemsLayoutPolicy.MeasureWrapped(sizes, availableSize.Width)
            : FilesGroupItemsLayoutPolicy.MeasureStack(sizes, availableSize.Width);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureOwner();
        var sizes = InternalChildren.Cast<UIElement>().Select(child => child.DesiredSize).ToArray();
        var bounds = IsIconMode
            ? FilesGroupItemsLayoutPolicy.ArrangeWrapped(sizes, finalSize.Width)
            : FilesGroupItemsLayoutPolicy.ArrangeStack(sizes, finalSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(bounds[i]);
        return finalSize;
    }

    private bool IsIconMode => _owner?.DisplayMode is FilesDisplayMode.LargeIcons
        or FilesDisplayMode.MediumIcons or FilesDisplayMode.SmallIcons or FilesDisplayMode.Tiles;

    private void EnsureOwner()
    {
        var owner = WpfTreeTraversal.FindAncestor<FrameworkElement>(
            this, element => element.DataContext is FilesColumnViewModel)?.DataContext as FilesColumnViewModel;

        if (ReferenceEquals(owner, _owner))
            return;
        if (_owner is not null)
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
        _owner = owner;
        if (_owner is not null)
            _owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    private void OnOwnerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilesColumnViewModel.DisplayMode))
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }
}
