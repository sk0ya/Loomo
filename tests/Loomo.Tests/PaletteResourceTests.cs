using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
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

    /// <summary>アクセント背景に載せる文字（AccentFg）が、実行時に
    /// <see cref="ThemeManager.AccentForeground"/> が作る色と一致すること。実行時は常に生成した色を
    /// 使うので、パレット側の手書き値がずれていると「ファイルを読んで分かる色」と「実際に出る色」が
    /// 食い違う（純白のまま置き去りになる、が実際に起きた）。</summary>
    [Fact]
    public void 各テーマのAccentFgがアクセントから生成した色と一致する()
    {
        RunSta(() =>
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                var dict = Load(theme);
                Color Color(string key) => ((SolidColorBrush)dict[key]).Color;

                var accent = Color("Accent");
                var expected = ThemeManager.AccentForeground(accent);
                Assert.True(expected == Color("AccentFg"),
                    $"{theme}: AccentFg が {Color("AccentFg")} だが、Accent {accent} からの生成値は {expected}");
            }
        });
    }

    /// <summary>アクセント背景・選択行背景の文字が読める明暗差を保つこと。アクセントはテーマごとに
    /// 明にも暗にもなる（Monokai の水色、Ayu の黄など）ので、白や黒を固定で置くと 1.5 前後まで落ちる。
    /// 4.5 は WCAG 2.x の AA（通常サイズ）、選択行は面積が大きいので 3.0 を下限にする。</summary>
    [Fact]
    public void アクセント背景と選択背景の文字色が読めるコントラストを保つ()
    {
        RunSta(() =>
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                var dict = Load(theme);
                Color Color(string key) => ((SolidColorBrush)dict[key]).Color;

                var onAccent = ThemeManager.Contrast(Color("Accent"), Color("AccentFg"));
                Assert.True(onAccent >= 4.5,
                    $"{theme}: Accent 背景 × AccentFg のコントラストが {onAccent:0.00}（4.50 未満）");

                var onSelection = ThemeManager.Contrast(Color("SelectionBg"), Color("Fg"));
                Assert.True(onSelection >= 3.0,
                    $"{theme}: SelectionBg 背景 × Fg のコントラストが {onSelection:0.00}（3.00 未満）");
            }
        });
    }

    /// <summary>ユーザーが任意のアクセントを指定できる（設定のカラー入力）ので、色相・彩度・明度を
    /// 総なめして生成した文字色が AA を満たすこと。中間の明るさ（スチール等）は白も黒も 4.1 前後で、
    /// 素朴な「白か黒か」の選択では届かない範囲がある。</summary>
    [Fact]
    public void 任意のアクセント色で生成した文字色がAAを満たす()
    {
        for (var h = 0; h < 360; h += 10)
            for (var s = 0.1; s <= 0.95; s += 0.2)
                for (var l = 0.15; l <= 0.9; l += 0.05)
                {
                    var accent = Hsl(h, s, l);
                    var fg = ThemeManager.AccentForeground(accent);
                    var contrast = ThemeManager.Contrast(accent, fg);
                    Assert.True(contrast >= 4.5,
                        $"hsl({h},{s:0.00},{l:0.00})={accent}: 生成した文字色 {fg} のコントラストが {contrast:0.00}");
                }
    }

    /// <summary>テスト用の HSL→RGB（ThemeManager 側は private なので、入力の生成だけ自前で行う）。</summary>
    private static Color Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
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
