using System;
using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Services.Infrastructure;

/// <summary>子メニューから親 ContextMenu の PlacementTarget にある DataContext を取得する。</summary>
internal static class WpfContextMenuDataContext
{
    public static object? Resolve(object sender, Func<object?> whenNotInContextMenu)
    {
        var current = sender as DependencyObject;
        while (current is MenuItem item)
            current = item.Parent;

        if (current is ContextMenu { PlacementTarget: FrameworkElement target })
            return target.DataContext;
        if (current is ContextMenu)
            return null;
        return whenNotInContextMenu();
    }
}
