using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧の列幅計算で使う、書体ごとの文字幅の物差し。</summary>
/// <remarks>
/// 1行ずつ <see cref="FormattedText"/> を作ると3000件のフォルダーで列1つ220msかかる（実測）。
/// ふだんはグリフの送り幅——WPF が実際に文字送りに使う値——を1文字ずつ覚えながら足し、
/// その書体に無い文字（絵文字など・別フォントで描かれる）を含む行だけ実測する。
/// 同じ3000件が4msになる。
/// </remarks>
internal readonly struct FilesColumnTextMeasure
{
    // 書体ごとの「文字→送り幅（em）」。UIスレッドからしか触らないので素の Dictionary でよい。
    private static readonly Dictionary<(string Family, int Weight, int Style), Dictionary<char, double>> Advances = new();

    private readonly Typeface _typeface;
    private readonly GlyphTypeface? _glyphs;
    private readonly Dictionary<char, double>? _advances;
    private readonly double _dpi;

    public FilesColumnTextMeasure(FontFamily family, FontStyle style, FontWeight weight, Visual owner)
    {
        _typeface = new Typeface(family, style, weight, FontStretches.Normal);
        _glyphs = _typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs : null;
        _dpi = VisualTreeHelper.GetDpi(owner).PixelsPerDip;
        if (_glyphs is null)
        {
            _advances = null;
            return;
        }

        var key = (family.Source, weight.ToOpenTypeWeight(), style == FontStyles.Normal ? 0 : 1);
        if (!Advances.TryGetValue(key, out var table))
            Advances[key] = table = new Dictionary<char, double>();
        _advances = table;
    }

    public double Width(string? text, double size)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        if (_glyphs is { } glyphs && _advances is { } advances)
        {
            var em = 0.0;
            var measured = true;
            foreach (var ch in text)
            {
                if (!advances.TryGetValue(ch, out var advance))
                {
                    advance = glyphs.CharacterToGlyphMap.TryGetValue(ch, out var index)
                        ? glyphs.AdvanceWidths[index]
                        : double.NaN;   // この書体に無い＝別フォントで描かれるので実測に回す
                    advances[ch] = advance;
                }
                if (double.IsNaN(advance))
                {
                    measured = false;
                    break;
                }
                em += advance;
            }

            if (measured)
                return em * size;
        }

        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _typeface, size, Brushes.Black, _dpi).WidthIncludingTrailingWhitespace;
    }
}
