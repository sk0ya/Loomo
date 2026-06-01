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
            return;
        }

        res[AccentKey] = new SolidColorBrush(color);
        res[AccentHoverKey] = new SolidColorBrush(Lighten(color, 0.18));
        res[AccentFgKey] = new SolidColorBrush(ContrastForeground(color));
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

    /// <summary>背景色の相対輝度から、その上に載せる文字色（白/黒）を選ぶ。</summary>
    private static Color ContrastForeground(Color bg)
    {
        // sRGB 相対輝度の簡易近似
        double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.55 ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White;
    }
}
