using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace sk0ya.Loomo.App.Services.Infrastructure;

/// <summary>WPFのVisual／Visual3D／Logical treeを辿る共通探索。</summary>
internal static class WpfTreeTraversal
{
    /// <summary>起点自身を含め、最も近い型一致の祖先を返す。</summary>
    public static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        => FindAncestor<T>(source, _ => true);

    /// <summary>起点自身を含め、条件を満たす最も近い型一致の祖先を返す。</summary>
    public static T? FindAncestor<T>(DependencyObject? source, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
            if (current is T match && predicate(match))
                return match;
        return null;
    }

    /// <summary>Visual tree上の最も近い祖先を返す（Logical treeへは移らない）。</summary>
    public static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match)
                return match;
        return null;
    }

    /// <summary>起点自身を含む親ツリーに、指定したインスタンスが含まれるか調べる。</summary>
    public static bool HasAncestor(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    /// <summary>Visual treeを深さ優先で探索する。起点自身は検索に含めない。</summary>
    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Visual treeでContextMenuを持つ最初の要素を深さ優先で探す。</summary>
    public static FrameworkElement? FindContextMenuHost(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: not null } element)
            return element;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindContextMenuHost(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;

        return null;
    }

    /// <summary>生成済みの項目コンテナーを深さ優先で探索する。</summary>
    public static T? FindItemContainer<T>(ItemsControl parent, Func<T, bool> predicate)
        where T : ItemsControl
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not T container)
                continue;
            if (predicate(container))
                return container;
            if (FindItemContainer(container, predicate) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>要素種別に応じたVisual／Logical treeの親を返す。</summary>
    public static DependencyObject? GetParent(DependencyObject current)
        => current is Visual or Visual3D
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);
}
