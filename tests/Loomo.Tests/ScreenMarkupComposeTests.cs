using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>画面に付けた印を、画像の画素の位置のまま 1 枚の PNG に焼く（§24.110 の 1）。</summary>
public class ScreenMarkupComposeTests
{
    [Fact]
    public void MarkLandsOnTheSamePixels_EvenWhenItIsTheOnlyThingAwayFromTheOrigin()
    {
        RunSta(() =>
        {
            var screen = Solid(200, 100, Colors.White);
            // 背景なしの層：中身の外接矩形が (50,40) から始まる。VisualBrush の既定のままだと左上へずれる。
            var layer = new Canvas { Width = 200, Height = 100 };
            var mark = new Rectangle { Width = 20, Height = 10, Fill = Brushes.Red };
            Canvas.SetLeft(mark, 50);
            Canvas.SetTop(mark, 40);
            layer.Children.Add(mark);
            layer.Measure(new Size(200, 100));
            layer.Arrange(new Rect(0, 0, 200, 100));

            var png = ScreenMarkupWindow.Compose(screen, layer);
            var decoded = Decode(png);

            Assert.Equal(200, decoded.PixelWidth);
            Assert.Equal(100, decoded.PixelHeight);
            Assert.Equal(Colors.Red, PixelAt(decoded, 60, 45));
            Assert.Equal(Colors.White, PixelAt(decoded, 45, 45));
            Assert.Equal(Colors.White, PixelAt(decoded, 5, 5));
        });
    }

    private static BitmapSource Solid(int w, int h, Color color)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    }

    private static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
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
}
