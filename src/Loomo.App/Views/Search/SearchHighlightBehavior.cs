using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;

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

        SearchHighlightRenderer.Render(
            textBlock, GetText(textBlock), GetQuery(textBlock),
            GetUseRegex(textBlock), GetCaseSensitive(textBlock));
    }
}
