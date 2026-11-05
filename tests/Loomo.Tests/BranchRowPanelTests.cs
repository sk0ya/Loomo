using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>ブランチ一覧の行（<see cref="BranchRowPanel"/>）：狭いときは名前を優先し、付記を右端（日時）から落とす。</summary>
public class BranchRowPanelTests
{
    // 現在印 10・名前 100・上流との差 20・日時 40
    [Theory]
    [InlineData(200, 100, 20, 40)] // 全部入る
    [InlineData(140, 100, 20, 0)]  // 日時から落とす（名前は削らない）
    [InlineData(125, 100, 0, 0)]   // 付記を全部落としても名前は全幅
    [InlineData(80, 70, 0, 0)]     // それでも足りないときだけ名前を縮める
    public void 名前を優先して付記を右端から落とす(double width, double name, double track, double date)
    {
        RunSta(() =>
        {
            var lead = Box(10);
            var nameBox = Box(100);
            var trackBox = Box(20);
            var dateBox = Box(40);
            var panel = new BranchRowPanel { Children = { lead, nameBox, trackBox, dateBox } };

            panel.Measure(new Size(width, 20));
            panel.Arrange(new Rect(0, 0, width, 20));

            Assert.Equal(name, Slot(nameBox));
            Assert.Equal(track, Slot(trackBox));
            Assert.Equal(date, Slot(dateBox));
        });
    }

    [Fact]
    public void 残した付記は右端に詰める()
    {
        RunSta(() =>
        {
            var nameBox = Box(50);
            var dateBox = Box(40);
            var panel = new BranchRowPanel { Children = { Box(10), nameBox, dateBox } };

            panel.Measure(new Size(300, 20));
            panel.Arrange(new Rect(0, 0, 300, 20));

            Assert.Equal(10, nameBox.TranslatePoint(new Point(), panel).X);
            Assert.Equal(260, dateBox.TranslatePoint(new Point(), panel).X);
        });
    }

    // 固定幅の子は配置幅に関係なく RenderSize が Width のままなので、パネルが割り当てた枠を見る。
    private static double Slot(UIElement child) => LayoutInformation.GetLayoutSlot((FrameworkElement)child).Width;

    private static Border Box(double width) => new() { Width = width, Height = 16 };

    private static void RunSta(Action body)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { ex = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }
}
