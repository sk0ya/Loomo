using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace sk0ya.Loomo.App.Views;

// TextBlock に「全文(Text)」と「強調クエリ(Query)」を添付し、Query に一致する部分文字列だけを
// 太字＋SearchHighlight 背景で描き分ける添付プロパティ。WPF では Inlines をバインドできないため、
// どれかが変わるたびに Inlines を組み直す。FolderTree のインクリメンタル検索と Search パネルの結果で使う。
// UseRegex=true のときは Query を正規表現として一致箇所を塗る（不正な式はハイライトせず素のまま）。
// CaseSensitive=true のときだけ大文字小文字を区別する（既定は区別しない）。
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

    public static readonly DependencyProperty UseRegexProperty =
        DependencyProperty.RegisterAttached(
            "UseRegex", typeof(bool), typeof(SearchHighlightBehavior),
            new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty CaseSensitiveProperty =
        DependencyProperty.RegisterAttached(
            "CaseSensitive", typeof(bool), typeof(SearchHighlightBehavior),
            new PropertyMetadata(false, OnChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    public static string GetQuery(DependencyObject obj) => (string)obj.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject obj, string value) => obj.SetValue(QueryProperty, value);

    public static bool GetUseRegex(DependencyObject obj) => (bool)obj.GetValue(UseRegexProperty);
    public static void SetUseRegex(DependencyObject obj, bool value) => obj.SetValue(UseRegexProperty, value);

    public static bool GetCaseSensitive(DependencyObject obj) => (bool)obj.GetValue(CaseSensitiveProperty);
    public static void SetCaseSensitive(DependencyObject obj, bool value) => obj.SetValue(CaseSensitiveProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var text = GetText(textBlock) ?? string.Empty;
        var query = GetQuery(textBlock) ?? string.Empty;

        textBlock.Inlines.Clear();

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        if (GetUseRegex(textBlock))
            BuildRegex(textBlock, text, query, GetCaseSensitive(textBlock));
        else
            BuildLiteral(textBlock, text, query, GetCaseSensitive(textBlock));
    }

    private static void BuildLiteral(TextBlock textBlock, string text, string query, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index < text.Length)
        {
            var hit = text.IndexOf(query, index, comparison);
            if (hit < 0)
            {
                textBlock.Inlines.Add(new Run(text[index..]));
                break;
            }

            if (hit > index)
                textBlock.Inlines.Add(new Run(text[index..hit]));

            AddHighlight(textBlock, text.Substring(hit, query.Length));
            index = hit + query.Length;
        }
    }

    private static void BuildRegex(TextBlock textBlock, string text, string query, bool caseSensitive)
    {
        Regex regex;
        try
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            regex = new Regex(query, options, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            // 不正な正規表現（入力途中など）はハイライトせず素のまま見せる。
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var index = 0;
        try
        {
            foreach (Match match in regex.Matches(text))
            {
                if (match.Index < index) // 念のため（ゼロ幅一致の巻き戻り対策）
                    continue;
                if (match.Index > index)
                    textBlock.Inlines.Add(new Run(text[index..match.Index]));

                if (match.Length > 0)
                {
                    AddHighlight(textBlock, match.Value);
                    index = match.Index + match.Length;
                }
                else
                {
                    index = match.Index; // ゼロ幅一致はスキップして無限ループを防ぐ
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // タイムアウトしたら以降は素のまま。
        }

        if (index < text.Length)
            textBlock.Inlines.Add(new Run(text[index..]));
    }

    private static void AddHighlight(TextBlock textBlock, string value)
    {
        var match = new Run(value) { FontWeight = FontWeights.Bold };
        match.SetResourceReference(TextElement.BackgroundProperty, "SearchHighlight");
        textBlock.Inlines.Add(match);
    }
}
