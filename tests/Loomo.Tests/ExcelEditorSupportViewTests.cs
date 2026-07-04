using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClosedXML.Excel;
using VGrid.Editor;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Excel の EditorSupport ビュー（ワークシートのタブ帯＋1つだけの VGrid）を実際に構築して、
/// タブが並ぶこと・グリッドは1つで内容だけ差し替わること・シート切替が効くことを確認する。
/// VGrid.Editor のテーマ pack URI 読み込みや TsvEditorControl の実体化も通る（＝起動時クラッシュの検出）。
/// </summary>
public class ExcelEditorSupportViewTests
{
    [Fact]
    public void ビュー_タブと単一グリッドを構築しシート切替できる()
    {
        RunStaWithDispatcher(() =>
        {
            var path = CreateTwoSheetXlsx();
            try
            {
                var provider = new ExcelEditorSupport(new AiSettings());
                var view = provider.GetOrCreateView();

                // 実アプリに近づけるため一度ウィンドウへ載せて測定・レイアウトさせる。
                var window = new Window { Width = 480, Height = 320, Content = view, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    PumpUntil(provider.UpdateAsync(path, text: ""));

                    // タブ帯はトップ Grid の直下の ListBox（グリッド内部の CommandPalette 等の
                    // ListBox と取り違えないよう直子から取る）。
                    var tabStrip = ((Grid)view).Children.OfType<ListBox>().FirstOrDefault();
                    var grids = CountDescendants<TsvEditorControl>(view);
                    var grid = FindDescendant<TsvEditorControl>(view);

                    Assert.NotNull(tabStrip);
                    Assert.NotNull(grid);
                    Assert.Equal(1, grids);                       // VGrid は1つだけ
                    Assert.Equal(2, tabStrip!.Items.Count);       // シートぶんのタブ
                    Assert.Equal("一枚目", tabStrip.Items[0]);
                    Assert.Equal("二枚目", tabStrip.Items[1]);

                    var firstTab = grid!.Tab;
                    Assert.NotNull(firstTab);

                    // シート切替：同じ1つのグリッドの中身（Tab）だけが差し替わる。
                    tabStrip.SelectedIndex = 1;
                    PumpDispatcher();
                    Assert.Equal(1, CountDescendants<TsvEditorControl>(view));
                    Assert.NotNull(grid.Tab);
                    Assert.NotSame(firstTab, grid.Tab);
                }
                finally { window.Close(); }
            }
            finally { File.Delete(path); }
        });
    }

    private static string CreateTwoSheetXlsx()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        using var wb = new XLWorkbook();
        var s1 = wb.AddWorksheet("一枚目");
        s1.Cell(1, 1).Value = "名前";
        s1.Cell(2, 1).Value = "太郎";
        var s2 = wb.AddWorksheet("二枚目");
        s2.Cell(1, 1).Value = "別シート";
        wb.SaveAs(path);
        return path;
    }

    private static int CountDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) count++;
            count += CountDescendants<T>(child);
        }
        return count;
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

    private static void RunStaWithDispatcher(Action body)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); }
            catch (Exception e) { ex = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }

    private static void PumpUntil(Task task)
    {
        while (!task.IsCompleted)
            PumpDispatcher();
        task.GetAwaiter().GetResult(); // 例外を観測
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
