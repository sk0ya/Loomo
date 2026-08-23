using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>グループ内の項目パネル。詳細・一覧は縦並び、アイコン系は折り返しを維持する。</summary>
public sealed class FilesGroupItemsPanel : Panel
{
    private FilesColumnViewModel? _owner;

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureOwner();
        if (!IsIconMode)
        {
            var width = 0d;
            var height = 0d;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;
            }
            return new Size(Math.Min(width, availableSize.Width), height);
        }

        var rowWidth = 0d;
        var rowHeight = 0d;
        var totalHeight = 0d;
        var totalWidth = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (rowWidth > 0 && rowWidth + child.DesiredSize.Width > availableSize.Width)
            {
                totalWidth = Math.Max(totalWidth, rowWidth);
                totalHeight += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }
            rowWidth += child.DesiredSize.Width;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        totalWidth = Math.Max(totalWidth, rowWidth);
        totalHeight += rowHeight;
        return new Size(Math.Min(totalWidth, availableSize.Width), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureOwner();
        if (!IsIconMode)
        {
            var y = 0d;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }
            return finalSize;
        }

        var x = 0d;
        var yIcon = 0d;
        var rowHeight = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var width = child.DesiredSize.Width;
            if (x > 0 && x + width > finalSize.Width)
            {
                x = 0;
                yIcon += rowHeight;
                rowHeight = 0;
            }
            child.Arrange(new Rect(x, yIcon, width, child.DesiredSize.Height));
            x += width;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        return finalSize;
    }

    private bool IsIconMode => _owner?.DisplayMode is FilesDisplayMode.LargeIcons
        or FilesDisplayMode.MediumIcons or FilesDisplayMode.SmallIcons or FilesDisplayMode.Tiles;

    private void EnsureOwner()
    {
        FilesColumnViewModel? owner = null;
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is FilesColumnViewModel candidate)
            {
                owner = candidate;
                break;
            }
            current = VisualTreeHelper.GetParent(current);
        }

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
