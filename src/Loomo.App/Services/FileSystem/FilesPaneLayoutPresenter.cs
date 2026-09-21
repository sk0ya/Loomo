using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>表示列数に応じて再利用する FilesColumnView を Grid へ配置する。</summary>
internal static class FilesPaneLayoutPresenter
{
    private const double SplitterThickness = 4;

    public static void Clear(Grid host)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
    }

    public static void Rebuild(
        Grid host,
        IReadOnlyList<FilesColumnView> views,
        FilesPaneViewModel viewModel,
        Brush border,
        Brush accent)
    {
        Clear(host);
        var count = Math.Clamp(viewModel.ColumnCount, 1, views.Count);
        var columns = count >= 2 ? 2 : 1;
        var rows = count == 4 ? 2 : 1;

        for (var c = 0; c < columns; c++)
        {
            if (c > 0)
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterThickness) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        }
        for (var r = 0; r < rows; r++)
        {
            if (r > 0)
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterThickness) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        }

        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            view.DataContext = i < viewModel.AllColumns.Count ? viewModel.AllColumns[i] : null;
            if (i >= count)
                continue;
            Grid.SetColumn(view, (i % columns) * 2);
            Grid.SetRow(view, (i / columns) * 2);
            host.Children.Add(view);
        }

        if (columns == 2)
            for (var r = 0; r < rows; r++)
                host.Children.Add(NewSplitter(border, accent, column: 1, row: r * 2, vertical: true));
        if (rows == 2)
            host.Children.Add(NewSplitter(border, accent, column: 0, row: 1,
                vertical: false, span: columns * 2 - 1));
    }

    private static GridSplitter NewSplitter(
        Brush border, Brush accent, int column, int row, bool vertical, int span = 1)
    {
        var splitter = new GridSplitter
        {
            Background = border,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
            ToolTip = "ドラッグでカラムの幅を変える",
        };
        splitter.MouseEnter += (_, _) => splitter.Background = accent;
        splitter.MouseLeave += (_, _) => splitter.Background = border;
        Grid.SetColumn(splitter, column);
        Grid.SetRow(splitter, row);
        if (!vertical)
            Grid.SetColumnSpan(splitter, Math.Max(1, span));
        return splitter;
    }
}
