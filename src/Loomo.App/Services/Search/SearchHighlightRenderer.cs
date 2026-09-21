using System.Windows.Controls;
using System.Windows.Documents;

namespace sk0ya.Loomo.App.Services;

/// <summary>検索語と一致範囲を TextBlock の Inlines へ描画する。</summary>
internal static class SearchHighlightRenderer
{
    internal static void Render(
        TextBlock textBlock, string? text, string? query, bool useRegex, bool caseSensitive)
    {
        text ??= string.Empty;
        query ??= string.Empty;
        textBlock.Inlines.Clear();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var index = 0;
        foreach (var match in SearchTextMatcher.FindMatches(text, query, useRegex, caseSensitive))
        {
            if (match.Start < index || match.Length <= 0)
                continue;
            if (match.Start > index)
                textBlock.Inlines.Add(new Run(text[index..match.Start]));
            var highlight = new Run(text.Substring(match.Start, match.Length)) { FontWeight = FontWeights.Bold };
            highlight.SetResourceReference(TextElement.BackgroundProperty, "SearchHighlight");
            textBlock.Inlines.Add(highlight);
            index = match.Start + match.Length;
        }

        if (index < text.Length)
            textBlock.Inlines.Add(new Run(text[index..]));
    }
}
