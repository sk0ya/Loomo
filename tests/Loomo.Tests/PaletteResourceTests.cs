using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// カラーテーマのパレット辞書（<c>Themes/Palette.&lt;AppTheme&gt;.xaml</c>）の健全性。
/// <see cref="AppTheme"/> に値を足してパレットを作り忘れる／キーを取りこぼすと、実行時に
/// その色だけ既定へフォールバックして「一部だけ配色が崩れたテーマ」になり、目視でしか気づけない。
/// ここでキー構成が Dark と一対一であることと、明暗判定（<see cref="AppThemeExtensions.IsLight"/>）が
/// 実際の背景色と食い違っていないことを機械的に押さえる。
/// </summary>
public class PaletteResourceTests
{
    [Fact]
    public void 全テーマのパレットが存在しキー構成がDarkと一致する()
    {
        RunSta(() =>
        {
            var baseline = Load(AppTheme.Dark).Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToArray();
            Assert.NotEmpty(baseline);

            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                var dict = Load(theme);
                var keys = dict.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToArray();
                Assert.True(baseline.SequenceEqual(keys),
                    $"{theme}: キーが Dark と一致しません（不足: {string.Join(",", baseline.Except(keys))} / " +
                    $"余分: {string.Join(",", keys.Except(baseline))}）");

                // 値はすべて SolidColorBrush（Controls.xaml 側が Background/Foreground へ直接流すため）。
                foreach (var key in keys)
                    Assert.True(dict[key] is SolidColorBrush, $"{theme}.{key} が SolidColorBrush ではありません");
            }
        });
    }

    [Fact]
    public void 明色テーマの判定が実際の背景色と一致する()
    {
        RunSta(() =>
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                var bg = ((SolidColorBrush)Load(theme)["Bg"]).Color;
                var luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                Assert.True(theme.IsLight() == luminance > 0.5,
                    $"{theme}: IsLight()={theme.IsLight()} だが背景 {bg} の明度は {luminance:0.00}");
            }
        });
    }

    private static ResourceDictionary Load(AppTheme theme)
    {
        // pack: の URI スキームと WebRequest ハンドラは、それぞれ PackUriHelper と Application の
        // 静的初期化で登録される。アプリ本体と違い Application インスタンスが無いテストでは、
        // 両方に触って初期化を促さないと UriFormatException / NotSupportedException になる。
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.ResourceAssembly;
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/sk0ya.Loomo.App;component/Themes/Palette.{theme}.xaml", UriKind.Absolute)
        };
    }

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
