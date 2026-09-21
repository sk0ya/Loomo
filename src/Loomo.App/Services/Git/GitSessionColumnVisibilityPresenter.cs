using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct GitBranchColumnLayoutState(GridLength Width, bool IsApplied);

/// <summary>Git セッションの列表示と、開いていた列幅の保存／復元を反映する。</summary>
internal static class GitSessionColumnVisibilityPresenter
{
    public static GitBranchColumnLayoutState ApplyBranchColumn(
        bool visible,
        bool? wasApplied,
        GridLength rememberedWidth,
        double columnActualWidth,
        double operationBarActualWidth,
        double collapsedWidth,
        ColumnDefinition splitterColumn,
        ColumnDefinition column,
        UIElement splitter,
        UIElement listArea,
        UIElement operationGroup)
    {
        if (visible)
        {
            splitterColumn.Width = new GridLength(6);
            column.MinWidth = 120;
            column.Width = rememberedWidth;
            splitter.Visibility = Visibility.Visible;
            listArea.Visibility = Visibility.Visible;
            operationGroup.Visibility = Visibility.Visible;
        }
        else
        {
            if (wasApplied != false && columnActualWidth > 0)
                rememberedWidth = new GridLength(columnActualWidth);
            splitter.Visibility = Visibility.Collapsed;
            listArea.Visibility = Visibility.Collapsed;
            operationGroup.Visibility = Visibility.Collapsed;
            splitterColumn.Width = new GridLength(0);
            var barWidth = operationBarActualWidth > 0 ? operationBarActualWidth : collapsedWidth;
            column.MinWidth = barWidth;
            column.Width = new GridLength(barWidth);
        }

        return new GitBranchColumnLayoutState(rememberedWidth, visible);
    }

    public static GridLength ApplyCommitDetailColumn(
        bool visible,
        GridLength rememberedWidth,
        double columnActualWidth,
        ColumnDefinition splitterColumn,
        ColumnDefinition column,
        UIElement splitter,
        UIElement panel)
    {
        if (visible)
        {
            splitterColumn.Width = new GridLength(6);
            column.MinWidth = 140;
            column.Width = rememberedWidth;
            splitter.Visibility = Visibility.Visible;
            panel.Visibility = Visibility.Visible;
            return rememberedWidth;
        }

        if (columnActualWidth > 0)
            rememberedWidth = new GridLength(columnActualWidth);
        splitter.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Collapsed;
        splitterColumn.Width = new GridLength(0);
        column.MinWidth = 0;
        column.Width = new GridLength(0);
        return rememberedWidth;
    }
}
