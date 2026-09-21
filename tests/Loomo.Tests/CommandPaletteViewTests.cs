using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コマンドパレットの箱（<see cref="CommandPaletteViewController"/>）の大きさ。
/// 探し方（コマンド／ファイル・テキスト・シンボル・行）を切り替えるたびに箱が伸び縮みしていた頃は、
/// 目で追っていた行が横へ流れて読み直しになっていた。大きさは探し方に依らないことをここで押さえる。
/// </summary>
[Collection(WpfViewTests.Name)]
public class CommandPaletteViewTests
{
    private readonly WpfViewHost _host;

    public CommandPaletteViewTests(WpfViewHost host) => _host = host;

    [Fact]
    public void 探し方を切り替えても箱の大きさは変わらない()
    {
        _host.Run(() =>
        {
            var box = new Border();
            var controller = new CommandPaletteViewController(
                new ListBox(), box, new PalettePreviewView(), new DataTemplate(), new DataTemplate());

            controller.UpdateSize(1280, 800);
            var width = box.Width;
            var maxHeight = box.MaxHeight;

            controller.SetNavigation(true);
            Assert.Equal(width, box.Width);
            Assert.Equal(maxHeight, box.MaxHeight);

            controller.SetNavigation(false);
            Assert.Equal(width, box.Width);
            Assert.Equal(maxHeight, box.MaxHeight);
        });
    }

    [Fact]
    public void 箱は被せている領域からはみ出さない()
    {
        _host.Run(() =>
        {
            var box = new Border();
            var controller = new CommandPaletteViewController(
                new ListBox(), box, new PalettePreviewView(), new DataTemplate(), new DataTemplate());

            // 袖やドックで領域が狭いとき——下限（1000×560）より領域を優先する。
            controller.UpdateSize(640, 420);
            Assert.True(box.Width <= 640, $"幅が領域を超えている: {box.Width}");
            Assert.True(box.MaxHeight <= 420, $"高さが領域を超えている: {box.MaxHeight}");
        });
    }
}
