using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>HSV のカラーピッカー（彩度×明度の面＋色相の帯＋16 進入力＋テーマの色から選ぶ）。
/// <see cref="SelectedColor"/> は双方向で、ドラッグ中も逐次返す（プレビューがその場で追従するように）。
/// 色相は内部に持ち続ける——RGB だけから戻すと、灰色（彩度 0）や黒（明度 0）に触れた瞬間に色相が 0 へ
/// 飛んで、面の色が赤に化ける。</summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Colors.Gray, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>「テーマの色から」に並べる色（ほかの欄の色）。</summary>
    public IEnumerable<PaletteChip>? Palette
    {
        get => PaletteList.ItemsSource as IEnumerable<PaletteChip>;
        set => PaletteList.ItemsSource = value;
    }

    public sealed record PaletteChip(string Label, Color Color)
    {
        public Brush Brush { get; } = new SolidColorBrush(Color);
    }

    private double _h, _s, _v;
    private bool _updating;

    public ColorPicker()
    {
        InitializeComponent();
        SvArea.SizeChanged += (_, _) => UpdateVisuals();
        HueArea.SizeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => { FromColor(SelectedColor, keepHue: false); UpdateVisuals(); };
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (picker._updating) return;
        picker.FromColor((Color)e.NewValue, keepHue: true);
        picker.UpdateVisuals();
    }

    private void FromColor(Color c, bool keepHue)
    {
        var (h, s, v) = ToHsv(c);
        if (!keepHue || s > 0.001 && v > 0.001) _h = h;
        _s = s;
        _v = v;
    }

    private void Commit()
    {
        _updating = true;
        SelectedColor = FromHsv(_h, _s, _v);
        _updating = false;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        HueFill.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        var w = SvArea.ActualWidth; var h = SvArea.ActualHeight;
        Canvas.SetLeft(SvHandle, _s * w - SvHandle.Width / 2);
        Canvas.SetTop(SvHandle, (1 - _v) * h - SvHandle.Height / 2);
        Canvas.SetLeft(HueHandle, _h / 360.0 * HueArea.ActualWidth - HueHandle.Width / 2);
        var color = FromHsv(_h, _s, _v);
        Preview.Background = new SolidColorBrush(color);
        SvHandle.Stroke = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0 > 0.6 ? Brushes.Black : Brushes.White;
        if (!HexBox.IsKeyboardFocused) HexBox.Text = UserTheme.Hex(color);
        HsvText.Text = $"H{_h:0} S{_s * 100:0} V{_v * 100:0}";
    }

    // ===== 彩度×明度の面 =====
    private void OnSvDown(object sender, MouseButtonEventArgs e) { SvArea.CaptureMouse(); PickSv(e.GetPosition(SvArea)); }
    private void OnSvMove(object sender, MouseEventArgs e) { if (SvArea.IsMouseCaptured) PickSv(e.GetPosition(SvArea)); }
    private void OnSvUp(object sender, MouseButtonEventArgs e) => SvArea.ReleaseMouseCapture();

    private void PickSv(Point p)
    {
        if (SvArea.ActualWidth <= 0 || SvArea.ActualHeight <= 0) return;
        _s = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
        _v = Math.Clamp(1 - p.Y / SvArea.ActualHeight, 0, 1);
        Commit();
    }

    // ===== 色相の帯 =====
    private void OnHueDown(object sender, MouseButtonEventArgs e) { HueArea.CaptureMouse(); PickHue(e.GetPosition(HueArea)); }
    private void OnHueMove(object sender, MouseEventArgs e) { if (HueArea.IsMouseCaptured) PickHue(e.GetPosition(HueArea)); }
    private void OnHueUp(object sender, MouseButtonEventArgs e) => HueArea.ReleaseMouseCapture();

    private void PickHue(Point p)
    {
        if (HueArea.ActualWidth <= 0) return;
        _h = Math.Clamp(p.X / HueArea.ActualWidth, 0, 1) * 360.0;
        if (_h >= 360) _h = 359.999;
        Commit();
    }

    // ===== 16 進入力・テーマの色 =====
    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHex();
        e.Handled = true;
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        if (UserThemeParser.TryParse(HexBox.Text, out var c))
        {
            FromColor(c, keepHue: true);
            Commit();
        }
        HexBox.Text = UserTheme.Hex(FromHsv(_h, _s, _v));
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Color c })
        {
            FromColor(c, keepHue: true);
            Commit();
        }
    }

    // ===== HSV ⇄ RGB =====
    internal static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    internal static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double t) => (byte)Math.Clamp(Math.Round(t * 255), 0, 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }
}
