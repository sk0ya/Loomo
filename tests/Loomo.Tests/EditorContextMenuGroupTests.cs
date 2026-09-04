using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エディタ右クリックメニューに Loomo が足す部分の<b>区切り線の作法</b>。
/// 以前は寄稿する <c>Add*</c> がそれぞれ <c>new Separator()</c> を先に足していたため、
/// .cs を右クリックすると区切り線が7本並び、条件次第で末尾が区切り線で終わった。
/// 今は <see cref="ShellWindow.AddMenuGroup"/> が「中身が入った束の前に1本だけ」入れる。
/// </summary>
public sealed class EditorContextMenuGroupTests
{
    [Fact]
    public void 中身が空の束には区切り線を入れない()
        => RunSta(() =>
        {
            var menu = new ContextMenu();

            ShellWindow.AddMenuGroup(menu, _ => { });

            Assert.Empty(menu.Items);
        });

    [Fact]
    public void 中身が入った束の前に区切り線を1本だけ入れる()
        => RunSta(() =>
        {
            var menu = new ContextMenu();

            ShellWindow.AddMenuGroup(menu, m =>
            {
                m.Items.Add(new MenuItem { Header = "A" });
                m.Items.Add(new MenuItem { Header = "B" });
            });

            Assert.IsType<Separator>(menu.Items[0]);
            Assert.Single(menu.Items.OfType<Separator>());
            Assert.Equal(3, menu.Items.Count);
        });

    /// <summary>空の束が挟まっても、区切り線が連続したり末尾に残ったりしない。</summary>
    [Fact]
    public void 空の束が挟まっても区切り線は連続しない()
        => RunSta(() =>
        {
            var menu = new ContextMenu();

            ShellWindow.AddMenuGroup(menu, m => m.Items.Add(new MenuItem { Header = "A" }));
            ShellWindow.AddMenuGroup(menu, _ => { });
            ShellWindow.AddMenuGroup(menu, m => m.Items.Add(new MenuItem { Header = "B" }));

            Assert.IsNotType<Separator>(menu.Items[^1]);
            for (int i = 1; i < menu.Items.Count; i++)
                Assert.False(menu.Items[i] is Separator && menu.Items[i - 1] is Separator,
                    "区切り線が連続している");
        });

    /// <summary>デバッグ項目はデバッガの管轄ソースにだけ出す
    /// （.md でブレークポイント操作を並べても押せるだけで何も起きない）。</summary>
    [Theory]
    [InlineData("a.cs", true)]
    [InlineData("a.ts", true)]
    [InlineData("a.tsx", true)]
    [InlineData("a.fs", true)]
    [InlineData("a.md", false)]
    [InlineData("a.json", false)]
    [InlineData("a.txt", false)]
    [InlineData("", false)]
    public void デバッグ項目の対象はデバッガの管轄拡張子だけ(string path, bool expected)
        => Assert.Equal(expected, ShellWindow.IsDebuggableSource(path));

    private static void RunSta(System.Action body)
    {
        System.Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (System.Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }
}
