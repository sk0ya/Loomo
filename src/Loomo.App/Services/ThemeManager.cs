using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.Services;

/// <summary>カラーテーマ（配色）とアクセントカラーの適用。
/// 配色は <see cref="Application"/> のマージ辞書から <c>Themes/Palette.*.xaml</c> を差し替える。
/// アクセントは <see cref="Application.Resources"/> 直下にブラシを置いてパレット定義を上書きする
/// （マージ辞書より直下のキーが優先されるため、テーマを切り替えても上書きは保持される）。
/// スタイル類は色を DynamicResource で参照しているため、いずれの変更も UI 全体へ即時反映される。</summary>
public sealed class ThemeManager
{
    private const string AccentKey = "Accent";
    private const string AccentHoverKey = "AccentHover";
    private const string AccentFgKey = "AccentFg";

    private AppTheme _theme = AppTheme.Dark;
    private string? _accent;

    /// <summary>テーマとアクセントをまとめて適用する（起動時に使用）。</summary>
    public void Apply(AppTheme theme, string? accentColor)
    {
        _theme = theme;
        _accent = accentColor;
        ApplyPalette(theme);
        ApplyAccent(accentColor);
    }

    /// <summary>テーマ（パレット）だけを切り替える。現在のアクセント上書きは維持する。</summary>
    public void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        ApplyPalette(theme);
        ApplyAccent(_accent);   // パレット差し替え後も上書きを保つ
    }

    /// <summary>アクセントカラーだけを切り替える。null/空ならテーマ既定へ戻す。</summary>
    public void ApplyAccentColor(string? accentColor)
    {
        _accent = accentColor;
        ApplyAccent(accentColor);
    }

    private static void ApplyPalette(AppTheme theme)
    {
        // ファイル種別アイコンは DynamicResource を経由しない（描画済みの DrawingImage なので）ため、
        // 明暗どちらの配色を使うかをここで教える。切り替わった場合はツリー側が引き直す。
        ViewModels.FileIcons.UseLightPalette = theme.IsLight();

        var app = Application.Current;
        if (app is null) return;

        var dict = LoadPalette(theme);
        // 対応するパレットが見つからない（列挙子追加漏れ・設定ファイル破損など）場合は
        // 既定の Dark へフォールバックし、起動時に画面が出ないまま落ちるのを防ぐ。
        if (dict is null && theme != AppTheme.Dark)
            dict = LoadPalette(AppTheme.Dark);
        if (dict is null) return;

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d =>
            d.Source is { } s && s.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            merged[merged.IndexOf(existing)] = dict;   // パレットは Controls.xaml より前に保つ
        else
            merged.Insert(0, dict);
    }

    /// <summary>パレット辞書を読み込む。リソースが存在しない場合は null を返す（例外は投げない）。</summary>
    private static ResourceDictionary? LoadPalette(AppTheme theme)
    {
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri($"Themes/Palette.{theme}.xaml", UriKind.Relative)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyAccent(string? accentColor)
    {
        var app = Application.Current;
        if (app is null) return;
        var res = app.Resources;

        if (!TryParseColor(accentColor, out var color))
        {
            // 上書きを解除してパレット既定のアクセントを露出させる
            res.Remove(AccentKey);
            res.Remove(AccentHoverKey);
            res.Remove(AccentFgKey);
            // 文字色はパレット既定のアクセントからも同じ規則で作る（手書き値とカスタム指定で
            // 見え方が変わらないよう、規則を一本にする）。Remove 済みなのでマージ辞書側が引ける。
            if (res[AccentKey] is SolidColorBrush palette)
                res[AccentFgKey] = new SolidColorBrush(AccentForeground(palette.Color));
            return;
        }

        res[AccentKey] = new SolidColorBrush(color);
        res[AccentHoverKey] = new SolidColorBrush(Lighten(color, 0.18));
        res[AccentFgKey] = new SolidColorBrush(AccentForeground(color));
    }

    /// <summary>指定文字列が有効なカラー指定（"#RRGGBB" 等）かどうか。</summary>
    public static bool IsValidColor(string? hex) => TryParseColor(hex, out _);

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color c) { color = c; return true; }
        }
        catch { /* 不正な文字列は上書きしない */ }
        return false;
    }

    /// <summary>白へ向けて <paramref name="amount"/>（0..1）の割合でブレンドする。</summary>
    private static Color Lighten(Color c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>アクセント背景の上に載せる文字色を作る。純白／純黒はアクセントから浮くため、
    /// 同じ色相のごく濃い色／ごく淡い色（彩度は抑える）を候補にし、コントラスト比の高い方を採る。
    /// <see cref="MinContrast"/> に届かない場合だけ、明度を白か黒へ寄せて確保する。</summary>
    public static Color AccentForeground(Color accent)
    {
        var (h, s, _) = ToHsl(accent);

        // 濃い側は彩度をしっかり残して色味を出す（0.40/0.12 では黒に近すぎて色が乏しかった）。
        // 淡い側は同じだけ彩度を上げると濁るので控えめにする。
        var dark = Pull(h, Math.Min(s, 0.55), 0.16, accent, toward: 0.0);
        var light = Pull(h, Math.Min(s, 0.28), 0.94, accent, toward: 1.0);
        return Contrast(accent, dark) >= Contrast(accent, light) ? dark : light;
    }

    /// <summary>読める明暗差になるまで明度を <paramref name="toward"/>（0=黒 / 1=白）へ寄せる。
    /// 中間色のアクセントでは端まで寄せても届かないことがあるので、その場合は端の色を返す。</summary>
    private static Color Pull(double h, double s, double l, Color accent, double toward)
    {
        var color = FromHsl(h, s, l);
        for (var i = 0; i < 24 && Contrast(accent, color) < MinContrast; i++)
        {
            l += (toward - l) * 0.25;
            color = FromHsl(h, s, l);
        }
        return color;
    }

    /// <summary>本文として読める下限（WCAG 2.x の AA、通常サイズ）。</summary>
    private const double MinContrast = 4.5;

    /// <summary>WCAG 2.x の相対輝度によるコントラスト比（1.0〜21.0）。</summary>
    public static double Contrast(Color a, Color b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        if (l2 > l1) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-9) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0)
              : max == g ? (b - r) / d + 2
              :            (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        if (s <= 0) return Gray(l);

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
            _   => (c, 0.0, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));

        static Color Gray(double v) => Color.FromRgb(Byte(v), Byte(v), Byte(v));
        static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
    }
}
