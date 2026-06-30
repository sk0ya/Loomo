using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public class HorizontalWheelScrollTests
{
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
