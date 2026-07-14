using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.Views;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>切り離しウィンドウの項目ホスティング（アクティブ切替・可視制御・破棄）の固定。UI 型を扱うため STA。</summary>
public class DetachedPaneWindowTests
{
    [Fact]
    public void 追加した項目はアクティブになり他は退避する()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var a = NewItem("A");
            var b = NewItem("B");

            window.AddItem(a);
            window.AddItem(b);

            Assert.Equal(2, window.ItemCount);
            Assert.False(a.IsActive);
            Assert.True(b.IsActive);
            Assert.Equal(Visibility.Collapsed, a.Content.Visibility);
            Assert.Equal(Visibility.Visible, b.Content.Visibility);

            window.SetActive(a);
            Assert.True(a.IsActive);
            Assert.False(b.IsActive);
            Assert.Equal(Visibility.Visible, a.Content.Visibility);
            Assert.Equal(Visibility.Collapsed, b.Content.Visibility);
        });
    }

    [Fact]
    public void 非アクティブ項目を破棄付きで外すと破棄されアクティブは保たれる()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var disposed = 0;
            var a = NewItem("A");
            var b = NewItem("B", dispose: () => disposed++);

            window.AddItem(a);
            window.AddItem(b);
            window.SetActive(a);       // A をアクティブに（B は非アクティブ）

            window.RemoveItem(b, dispose: true);

            Assert.Equal(1, disposed);
            Assert.Equal(1, window.ItemCount);
            Assert.True(a.IsActive);   // 非アクティブ側を外したのでアクティブは動かない
        });
    }

    [Fact]
    public void 移動用に破棄なしで外すと破棄されない()
    {
        RunSta(() =>
        {
            var window = NewWindow();
            var disposed = 0;
            var a = NewItem("A");
            var b = NewItem("B", dispose: () => disposed++);
            window.AddItem(a);
            window.AddItem(b);

            window.RemoveItem(b, dispose: false);

            Assert.Equal(0, disposed);
            Assert.Equal(1, window.ItemCount);
            Assert.False(window.Contains(b));
        });
    }

    [Fact]
    public void 項目のDisposeは冪等()
    {
        RunSta(() =>
        {
            var count = 0;
            var item = NewItem("X", dispose: () => count++);
            item.Dispose();
            item.Dispose();
            Assert.Equal(1, count);
        });
    }

    private static DetachedPaneWindow NewWindow()
        => new(new DetachedWindowManager(new Window()));

    private static DetachedItem NewItem(string title, Action? dispose = null)
        => new(DetachKind.EditorMirror, title, new Border(), icon: null, dispose: dispose);

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
