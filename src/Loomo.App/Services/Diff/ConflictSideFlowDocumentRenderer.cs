using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>競合解決の左右本文・行番号を FlowDocument へ描画する。</summary>
internal static class ConflictSideFlowDocumentRenderer
{
    private static readonly Brush OursDistinctBg = DiffFlowDocumentRenderer.FrozenBrush("#1F4CAF50");
    private static readonly Brush OursDistinctFg = DiffFlowDocumentRenderer.FrozenBrush("#FF81C784");
    private static readonly Brush TheirsDistinctBg = DiffFlowDocumentRenderer.FrozenBrush("#1FE57373");
    private static readonly Brush TheirsDistinctFg = DiffFlowDocumentRenderer.FrozenBrush("#FFE57373");

    private const double FontSizePx = 12.0;
    private static readonly Typeface MonoTypeface = new(
        new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    internal static FlowDocument Build(IReadOnlyList<ConflictSideLineVm>? lines, string? mode)
    {
        if (mode == "Gutter")
            return BuildGutterDocument(lines);

        var pageWidth = MonospacePageWidth.Measure(
            lines?.Select(line => line.Text) ?? Enumerable.Empty<string>(),
            MonoTypeface, FontSizePx, pixelsPerDip: 1.0);
        return BuildContentDocument(lines, isTheirs: mode == "Theirs", pageWidth);
    }

    private static FlowDocument NewDocument(double? pageWidth = null)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = FontSizePx,
        };
        if (pageWidth is double width)
            document.MinPageWidth = width;
        return document;
    }

    private static Paragraph NewParagraph() => new()
    {
        Margin = new Thickness(0),
        LineHeight = DiffFlowDocumentRenderer.LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static FlowDocument BuildContentDocument(
        IReadOnlyList<ConflictSideLineVm>? lines, bool isTheirs, double pageWidth)
    {
        var document = NewDocument(pageWidth);
        var (distinctBackground, distinctForeground) = isTheirs
            ? (TheirsDistinctBg, TheirsDistinctFg)
            : (OursDistinctBg, OursDistinctFg);

        if (lines is null || lines.Count == 0)
        {
            var placeholder = NewParagraph();
            var run = new Run("（このコンフリクトでは削除されました）") { FontStyle = FontStyles.Italic };
            run.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
            placeholder.Inlines.Add(run);
            document.Blocks.Add(placeholder);
            return document;
        }

        foreach (var line in lines)
        {
            var paragraph = NewParagraph();
            var run = new Run(line.Text);
            if (line.Kind == "Distinct")
            {
                run.Foreground = distinctForeground;
                paragraph.Background = distinctBackground;
            }
            else
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            }
            paragraph.Inlines.Add(run);
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private static FlowDocument BuildGutterDocument(IReadOnlyList<ConflictSideLineVm>? lines)
    {
        var document = NewDocument();
        if (lines is null || lines.Count == 0)
        {
            document.Blocks.Add(GutterParagraph(""));
            return document;
        }
        foreach (var line in lines)
            document.Blocks.Add(GutterParagraph(line.LineNumber.ToString(CultureInfo.InvariantCulture)));
        return document;
    }

    private static Paragraph GutterParagraph(string number)
    {
        var paragraph = NewParagraph();
        paragraph.TextAlignment = TextAlignment.Right;
        paragraph.Padding = new Thickness(0, 0, 6, 0);
        var run = new Run(number);
        run.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
        paragraph.Inlines.Add(run);
        return paragraph;
    }

}
