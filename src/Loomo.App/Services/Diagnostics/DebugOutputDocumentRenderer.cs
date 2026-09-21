using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ出力行をテーマ追従の FlowDocument 段落へ変換する。</summary>
internal static class DebugOutputDocumentRenderer
{
    internal static void Append(FlowDocument document, DebugOutputLine line)
    {
        var run = new Run(line.Text);
        var style = DebugOutputPresentation.For(line.Category);
        run.SetResourceReference(TextElement.ForegroundProperty, style.ForegroundResource);
        if (style.Emphasized)
            run.FontWeight = FontWeights.SemiBold;
        document.Blocks.Add(new Paragraph(run) { Margin = new Thickness(0) });
    }
}
