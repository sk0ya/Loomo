using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public class HorizontalWheelScrollTests
{
    [Fact]
    public void DataGridの内部ScrollViewerを横スクロールできる()
    {
        // Excel プレビューは VGrid（内部は WPF DataGrid）で表示する。カーソル直下から辿った
        // DataGrid 内部の ScrollViewer を、既存の WM_MOUSEHWHEEL フックが横スクロールできることを固定する。
        RunSta(() =>
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            for (int c = 0; c < 12; c++)
                grid.Columns.Add(new DataGridTextColumn { Header = $"C{c}", Width = 120 });
            grid.ItemsSource = Enumerable.Range(0, 5).Select(_ => new { }).ToList();

            var window = new Window { Width = 320, Height = 180, Content = grid, ShowInTaskbar = false };
            try
            {
                window.Show();
                PumpDispatcher();

                var viewer = FindDescendant<ScrollViewer>(grid);
                Assert.NotNull(viewer);
                Assert.True(viewer!.ScrollableWidth > 0, $"ScrollableWidth={viewer.ScrollableWidth}");

                Assert.True(HorizontalWheelScroll.Handle(viewer, 120), "横ホイール入力が未処理になった");
                PumpDispatcher();
                Assert.True(viewer.HorizontalOffset > 0, $"HorizontalOffset={viewer.HorizontalOffset}");
            }
            finally { window.Close(); }
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    [Fact]
    public void RichTextBox自体が入力元でも内部ScrollViewerを横スクロールできる()
    {
        RunSta(() =>
        {
            var box = new RichTextBox
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Document = new FlowDocument(new Paragraph(new Run(new string('x', 300))))
                {
                    PageWidth = 2400,
                    PagePadding = new Thickness(0),
                },
            };
            var window = new Window { Width = 320, Height = 180, Content = box, ShowInTaskbar = false };
            try
            {
                window.Show();
                PumpDispatcher();

                var viewer = (ScrollViewer?)box.Template.FindName("PART_ContentHost", box);
                Assert.NotNull(viewer);
                Assert.True(viewer!.ScrollableWidth > 0,
                    $"ScrollableWidth={viewer.ScrollableWidth}, ExtentWidth={viewer.ExtentWidth}, ViewportWidth={viewer.ViewportWidth}, PageWidth={box.Document.PageWidth}");

                Assert.True(HorizontalWheelScroll.Handle(box, 120), "横ホイール入力が未処理になった");
                PumpDispatcher();
                Assert.True(viewer.HorizontalOffset > 0, $"HorizontalOffset={viewer.HorizontalOffset}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null) throw exception;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
