using Editor.Core.Syntax;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct DiffUnifiedDocumentBuild(FlowDocument Document, ChunkedAppendState Build);

internal readonly record struct DiffSideDocumentBuild(
    FlowDocument Left, FlowDocument Right, FlowDocument LeftNumbers, FlowDocument RightNumbers,
    ChunkedAppendState Build);

/// <summary>差分行を FlowDocument へ描画し、幅を測る。</summary>
internal sealed class DiffFlowDocumentRenderer
{
    internal const double LineHeight = 16.0;
    private static readonly Brush AddedBackground = FrozenBrush("#1F4CAF50");
    private static readonly Brush AddedForeground = FrozenBrush("#FF81C784");
    private static readonly Brush RemovedBackground = FrozenBrush("#1FE57373");
    private static readonly Brush RemovedForeground = FrozenBrush("#FFE57373");
    private static readonly Brush EmptyBackground = FrozenBrush("#14808080");
    private static readonly Func<TokenKind, Brush?> ThemeForeground = EditorSyntaxColors.Foreground;

    private readonly FrameworkElement _owner;
    private Typeface? _monoTypeface;

    internal DiffFlowDocumentRenderer(FrameworkElement owner) => _owner = owner;

    internal DiffUnifiedDocumentBuild BuildUnified(
        IReadOnlyList<DiffRowVm> rows, IReadOnlyList<SyntaxToken[]?> syntax)
    {
        var document = NewDocument(MeasureMaxWidth(rows.Select(row => row.Text)));
        var build = new ChunkedAppendState(rows.Count, (start, end) =>
        {
            for (var index = start; index < end; index++)
                document.Blocks.Add(TextParagraph(rows[index].Text, rows[index].Kind, TokensAt(syntax, index)));
        });
        return new DiffUnifiedDocumentBuild(document, build);
    }

    internal DiffSideDocumentBuild BuildSide(
        IReadOnlyList<DiffSideRowVm> rows,
        IReadOnlyList<SyntaxToken[]?> leftSyntax,
        IReadOnlyList<SyntaxToken[]?> rightSyntax)
    {
        var width = MeasureMaxWidth(rows.SelectMany(row => new[] { row.LeftText, row.RightText }));
        var left = NewDocument(width);
        var right = NewDocument(width);
        var leftNumbers = NewDocument(null);
        var rightNumbers = NewDocument(null);
        var build = new ChunkedAppendState(rows.Count, (start, end) =>
        {
            for (var index = start; index < end; index++)
            {
                var row = rows[index];
                left.Blocks.Add(TextParagraph(row.LeftText, row.LeftKind, TokensAt(leftSyntax, index)));
                right.Blocks.Add(TextParagraph(row.RightText, row.RightKind, TokensAt(rightSyntax, index)));
                leftNumbers.Blocks.Add(GutterParagraph(row.LeftLine));
                rightNumbers.Blocks.Add(GutterParagraph(row.RightLine));
            }
        });
        return new DiffSideDocumentBuild(left, right, leftNumbers, rightNumbers, build);
    }

    internal static List<Run> SyntaxRuns(
        string text, SyntaxToken[] tokens, Func<TokenKind, Brush?>? foreground = null)
    {
        foreground ??= ThemeForeground;
        var segments = DiffSyntaxRunMapper.Map(text, tokens, token => foreground(token));
        var runs = new List<Run>(segments.Count);
        foreach (var segment in segments)
        {
            var run = new Run(text[segment.Start..segment.End]);
            if (segment.ForegroundKey is Brush brush) run.Foreground = brush;
            else run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            runs.Add(run);
        }
        return runs;
    }

    private FlowDocument NewDocument(double? pageWidth)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = UiFontManager.Scaled(12),
        };
        if (pageWidth is double width) document.PageWidth = width;
        return document;
    }

    private double MeasureMaxWidth(IEnumerable<string> lines)
    {
        var typeface = _monoTypeface ??= new Typeface(
            new FontFamily("Cascadia Mono, Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return MonospacePageWidth.Measure(
            lines, typeface, UiFontManager.Scaled(12), VisualTreeHelper.GetDpi(_owner).PixelsPerDip);
    }

    private static Paragraph TextParagraph(string text, string kind, SyntaxToken[]? tokens = null)
    {
        var paragraph = NewParagraph();
        paragraph.Background = kind switch
        {
            "Added" => AddedBackground,
            "Removed" => RemovedBackground,
            "Empty" => EmptyBackground,
            _ => null,
        };
        if (tokens is { Length: > 0 })
        {
            paragraph.Inlines.AddRange(SyntaxRuns(text, tokens));
            return paragraph;
        }

        var run = new Run(text);
        switch (kind)
        {
            case "Added": run.Foreground = AddedForeground; break;
            case "Removed": run.Foreground = RemovedForeground; break;
            case "Gap": run.SetResourceReference(TextElement.ForegroundProperty, "Accent"); break;
            case "Header": run.SetResourceReference(TextElement.ForegroundProperty, "FgDim"); break;
            default: run.SetResourceReference(TextElement.ForegroundProperty, "Fg"); break;
        }
        paragraph.Inlines.Add(run);
        return paragraph;
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

    private static Paragraph NewParagraph() => new()
    {
        Margin = new Thickness(0),
        LineHeight = LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static SyntaxToken[]? TokensAt(IReadOnlyList<SyntaxToken[]?> syntax, int index)
        => index < syntax.Count ? syntax[index] : null;

    internal static Brush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
