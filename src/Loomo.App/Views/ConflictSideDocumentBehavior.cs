using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

// Git競合解決画面（DiffSessionView）の Ours/Theirs ペイン用。RichTextBox.Document は Binding を直接
// 設定できない（DependencyProperty だが XAML の Binding 経由の設定を拒む）ため、SearchHighlightBehavior
// と同じ考え方で「行データが変わるたびに Document を組み直す」添付プロパティにする。
// Mode は "Gutter"（行番号のみ）/ "Ours"（本文・差分は緑）/ "Theirs"（本文・差分は赤）のいずれか。
public static class ConflictSideDocumentBehavior
{
    public static readonly DependencyProperty LinesProperty =
        DependencyProperty.RegisterAttached(
            "Lines", typeof(IReadOnlyList<ConflictSideLineVm>), typeof(ConflictSideDocumentBehavior),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode", typeof(string), typeof(ConflictSideDocumentBehavior),
            new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<ConflictSideLineVm>? GetLines(DependencyObject obj) =>
        (IReadOnlyList<ConflictSideLineVm>?)obj.GetValue(LinesProperty);
    public static void SetLines(DependencyObject obj, IReadOnlyList<ConflictSideLineVm>? value) =>
        obj.SetValue(LinesProperty, value);

    public static string? GetMode(DependencyObject obj) => (string?)obj.GetValue(ModeProperty);
    public static void SetMode(DependencyObject obj, string? value) => obj.SetValue(ModeProperty, value);

    // 通常差分の左右並び表示（AddedBg/AddedFg/RemovedBg/RemovedFg）と同じ固定色。conflict では
    // 新旧ではなく列の身元（Ours=緑ヘッダー／Theirs=赤ヘッダー）に揃えて、差分行だけ強調する。
    private static readonly Brush OursDistinctBg = Freeze("#1F4CAF50");
    private static readonly Brush OursDistinctFg = Freeze("#FF81C784");
    private static readonly Brush TheirsDistinctBg = Freeze("#1FE57373");
    private static readonly Brush TheirsDistinctFg = Freeze("#FFE57373");

    private const double PageWidthPadding = 24.0; // 本文右端の余白（横スクロールの行き過ぎ防止）
    private const double MinContentWidth = 200.0; // 計測不能時の最小ページ幅
    private const double FontSizePx = 12.0;
    private static readonly Typeface MonoTypeface = new(
        new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box) return;
        var lines = GetLines(box);
        var mode = GetMode(box);
        if (mode == "Gutter")
        {
            // ガター側は固定幅の RichTextBox（Width=30）に乗せるので、内容幅を明示しなくても初回計測から
            // 安定する（本文側と違い Star 列の初回計測で幅が確定しない問題が起きない）。
            box.Document = BuildGutterDocument(lines);
            return;
        }
        // 本文側は Star 列の RichTextBox。FlowDocument に明示の最小ページ幅を与えないと、初回計測時に
        // 極端に狭い幅で確定してしまい、長い行が1文字ずつ折り返される（通常差分の左右並び表示と同じ理由で
        // MeasureMaxWidth 相当の実測値を明示する。DiffSessionView.xaml.cs の MeasureMaxWidth と同じ考え方）。
        // PageWidth（固定）ではなく MinPageWidth にする：ペインが内容より広いときは PageWidth=NaN の
        // 既定でページがペイン幅まで広がり左寄せのまま（固定だとページがビューア中央に置かれ、短い内容が
        // 中央に浮いて見える）。内容が広いときは最小幅が効いて横スクロールになる。
        var pageWidth = MeasureMaxWidth(lines?.Select(l => l.Text) ?? Enumerable.Empty<string>());
        box.Document = BuildContentDocument(lines, isTheirs: mode == "Theirs", pageWidth);
    }

    private static double MeasureMaxWidth(IEnumerable<string> lines)
    {
        var max = 0.0;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var ft = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                MonoTypeface, FontSizePx, Brushes.Black, 1.0);
            if (ft.WidthIncludingTrailingWhitespace > max) max = ft.WidthIncludingTrailingWhitespace;
        }
        return Math.Max(MinContentWidth, max + PageWidthPadding);
    }

    private static FlowDocument NewDocument(double? pageWidth = null)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = FontSizePx,
        };
        if (pageWidth is double w) doc.MinPageWidth = w;
        return doc;
    }

    private static Paragraph NewParagraph() => new()
    {
        Margin = new Thickness(0),
        LineHeight = 16.0,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static FlowDocument BuildContentDocument(IReadOnlyList<ConflictSideLineVm>? lines, bool isTheirs, double pageWidth)
    {
        var doc = NewDocument(pageWidth);
        var (distinctBg, distinctFg) = isTheirs ? (TheirsDistinctBg, TheirsDistinctFg) : (OursDistinctBg, OursDistinctFg);

        if (lines is null || lines.Count == 0)
        {
            var placeholder = NewParagraph();
            var placeholderRun = new Run("（このコンフリクトでは削除されました）") { FontStyle = FontStyles.Italic };
            placeholderRun.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
            placeholder.Inlines.Add(placeholderRun);
            doc.Blocks.Add(placeholder);
            return doc;
        }

        foreach (var line in lines)
        {
            var p = NewParagraph();
            var run = new Run(line.Text);
            if (line.Kind == "Distinct")
            {
                run.Foreground = distinctFg;
                p.Background = distinctBg;
            }
            else
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            }
            p.Inlines.Add(run);
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static FlowDocument BuildGutterDocument(IReadOnlyList<ConflictSideLineVm>? lines)
    {
        var doc = NewDocument();
        if (lines is null || lines.Count == 0)
        {
            doc.Blocks.Add(GutterParagraph(""));
            return doc;
        }
        foreach (var line in lines)
            doc.Blocks.Add(GutterParagraph(line.LineNumber.ToString(CultureInfo.InvariantCulture)));
        return doc;
    }

    private static Paragraph GutterParagraph(string number)
    {
        var p = NewParagraph();
        p.TextAlignment = TextAlignment.Right;
        p.Padding = new Thickness(0, 0, 6, 0);
        var run = new Run(number);
        run.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
        p.Inlines.Add(run);
        return p;
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
