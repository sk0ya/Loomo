using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace sk0ya.Loomo.App.Views;

// TextBlock に「全文(Text)」と「強調クエリ(Query)」を添付し、Query に一致する部分文字列だけを
// 太字＋SearchHighlight 背景で描き分ける添付プロパティ。WPF では Inlines をバインドできないため、
// どちらかが変わるたびに Inlines を組み直す。FolderTree のインクリメンタル検索で使う。
public static class SearchHighlightBehavior
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(SearchHighlightBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached(
            "Query", typeof(string), typeof(SearchHighlightBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    public static string GetQuery(DependencyObject obj) => (string)obj.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject obj, string value) => obj.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var text = GetText(textBlock) ?? string.Empty;
        var query = GetQuery(textBlock) ?? string.Empty;

        textBlock.Inlines.Clear();

        if (string.IsNullOrEmpty(query))
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var index = 0;
        while (index < text.Length)
        {
            var hit = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                textBlock.Inlines.Add(new Run(text[index..]));
                break;
            }

            if (hit > index)
                textBlock.Inlines.Add(new Run(text[index..hit]));

            var match = new Run(text.Substring(hit, query.Length)) { FontWeight = FontWeights.Bold };
            match.SetResourceReference(TextElement.BackgroundProperty, "SearchHighlight");
            textBlock.Inlines.Add(match);

            index = hit + query.Length;
        }
    }
}
