using System.Collections.Generic;
using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧グループ内の項目を縦並び／折り返しで配置する幾何計算。</summary>
internal static class FilesGroupItemsLayoutPolicy
{
    public static Size MeasureStack(IReadOnlyList<Size> items, double availableWidth)
    {
        var width = 0d;
        var height = 0d;
        foreach (var item in items)
        {
            width = Math.Max(width, item.Width);
            height += item.Height;
        }
        return new Size(Math.Min(width, availableWidth), height);
    }

    public static Size MeasureWrapped(IReadOnlyList<Size> items, double availableWidth)
    {
        var rowWidth = 0d;
        var rowHeight = 0d;
        var totalWidth = 0d;
        var totalHeight = 0d;
        foreach (var item in items)
        {
            if (rowWidth > 0 && rowWidth + item.Width > availableWidth)
            {
                totalWidth = Math.Max(totalWidth, rowWidth);
                totalHeight += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }
            rowWidth += item.Width;
            rowHeight = Math.Max(rowHeight, item.Height);
        }
        return new Size(Math.Min(Math.Max(totalWidth, rowWidth), availableWidth), totalHeight + rowHeight);
    }

    public static Rect[] ArrangeStack(IReadOnlyList<Size> items, double width)
    {
        var bounds = new Rect[items.Count];
        var y = 0d;
        for (var i = 0; i < items.Count; i++)
        {
            bounds[i] = new Rect(0, y, width, items[i].Height);
            y += items[i].Height;
        }
        return bounds;
    }

    public static Rect[] ArrangeWrapped(IReadOnlyList<Size> items, double availableWidth)
    {
        var bounds = new Rect[items.Count];
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (x > 0 && x + item.Width > availableWidth)
            {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }
            bounds[i] = new Rect(x, y, item.Width, item.Height);
            x += item.Width;
            rowHeight = Math.Max(rowHeight, item.Height);
        }
        return bounds;
    }
}
