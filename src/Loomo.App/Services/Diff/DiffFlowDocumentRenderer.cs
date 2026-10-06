using Editor.Core.Syntax;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct DiffUnifiedDocumentBuild(FlowDocument Document, ChunkedAppendState Build);

/// <summary>差分行を FlowDocument へ描画し、幅を測る。</summary>
internal sealed class DiffFlowDocumentRenderer
{
    internal const double LineHeight = 16.0;
    private static readonly Brush AddedBackground = FrozenBrush("#1F4CAF50");
    private static readonly Brush AddedForeground = FrozenBrush("#FF81C784");
    private static readonly Brush RemovedBackground = FrozenBrush("#1FE57373");
    private static readonly Brush RemovedForeground = FrozenBrush("#FFE57373");
    // 行内差分（行の中で実際に変わった文字）。行の背景と同じ色相をずっと濃くして、行の色の上に重ねる。
    private static readonly Brush AddedChangeBackground = FrozenBrush("#664CAF50");
    private static readonly Brush RemovedChangeBackground = FrozenBrush("#66E57373");
    private static readonly Func<TokenKind, Brush?> ThemeForeground = EditorSyntaxColors.Foreground;

    private readonly FrameworkElement _owner;
    private Typeface? _monoTypeface;

    internal DiffFlowDocumentRenderer(FrameworkElement owner) => _owner = owner;

    internal DiffUnifiedDocumentBuild BuildUnified(
        IReadOnlyList<DiffRowVm> rows, IReadOnlyList<SyntaxToken[]?> syntax,
        IReadOnlyList<IReadOnlyList<TextSpan>?>? inline = null)
    {
        inline ??= DiffInlineHighlighter.None;
        // 左端のステージの帯（枠3＋余白3）のぶん広げる。足さないと最長の行が折り返す。
        var document = NewDocument(MeasureMaxWidth(rows.Select(row => row.Text)) + StagedBarWidth);
        var build = new ChunkedAppendState(rows.Count, (start, end) =>
        {
            for (var index = start; index < end; index++)
                document.Blocks.Add(TextParagraph(
                    rows[index].Text, rows[index].Kind, TokensAt(syntax, index), rows[index].Staged,
                    index < inline.Count ? inline[index] : null));
        });
        return new DiffUnifiedDocumentBuild(document, build);
    }

    internal static List<Run> SyntaxRuns(
        string text, SyntaxToken[] tokens, Func<TokenKind, Brush?>? foreground = null,
        IReadOnlyList<TextSpan>? changes = null, Brush? changeBackground = null)
    {
        foreground ??= ThemeForeground;
        var segments = DiffSyntaxRunMapper.SplitByChanges(
            DiffSyntaxRunMapper.Map(text, tokens, token => foreground(token)), changes);
        var runs = new List<Run>(segments.Count);
        foreach (var segment in segments)
        {
            var run = new Run(text[segment.Start..segment.End]);
            if (segment.ForegroundKey is Brush brush) run.Foreground = brush;
            else run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            if (segment.Changed && changeBackground is not null) run.Background = changeBackground;
            runs.Add(run);
        }
        return runs;
    }

    /// <summary>行内差分の範囲だけ背景を濃くした、構文色なしの Run 列。</summary>
    internal static List<Run> PlainRuns(string text, IReadOnlyList<TextSpan> changes, Brush foreground, Brush changeBackground)
    {
        var whole = new[] { new DiffSyntaxRun(0, text.Length, null) };
        var runs = new List<Run>();
        foreach (var segment in DiffSyntaxRunMapper.SplitByChanges(whole, changes))
        {
            var run = new Run(text[segment.Start..segment.End]) { Foreground = foreground };
            if (segment.Changed) run.Background = changeBackground;
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

    /// <summary>ステージ済みの行の左端の帯（左右並びの中央の帯と同じ色）。追加／削除の色はそのまま残す——
    /// 「何が変わったか」と「インデックスに入ったか」は別の軸。</summary>
    private static readonly Brush StagedBar = FrozenBrush("#FF42A5F5");
    private const double StagedBarWidth = 6;

    private static Paragraph TextParagraph(
        string text, string kind, SyntaxToken[]? tokens = null, bool staged = false,
        IReadOnlyList<TextSpan>? changes = null)
    {
        var paragraph = NewParagraph();
        // 帯の幅ぶん、ステージしていない行も左を空けて桁を揃える。
        paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
        paragraph.Padding = new Thickness(3, 0, 0, 0);
        paragraph.BorderBrush = staged ? StagedBar : Brushes.Transparent;
        paragraph.Background = kind switch
        {
            "Added" => AddedBackground,
            "Removed" => RemovedBackground,
            _ => null,
        };
        var changeBackground = kind switch
        {
            "Added" => AddedChangeBackground,
            "Removed" => RemovedChangeBackground,
            _ => null,
        };
        if (tokens is { Length: > 0 })
        {
            paragraph.Inlines.AddRange(SyntaxRuns(text, tokens, changes: changes, changeBackground: changeBackground));
            return paragraph;
        }
        if (changes is { Count: > 0 } && changeBackground is not null)
        {
            paragraph.Inlines.AddRange(PlainRuns(
                text, changes, kind == "Added" ? AddedForeground : RemovedForeground, changeBackground));
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
