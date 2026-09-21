using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コマンドパレット一覧のタイトルを、現在のクエリに一致した文字だけ強調（地色＋太字）して描画する
/// 添付ビヘイビア。<see cref="TextProperty"/>（タイトル）と <see cref="QueryProperty"/>（素のクエリ）を
/// TextBlock にバインドすると、その Inlines を組み直す。一致判定は <see cref="PaletteFilter"/> と揃え、
/// 部分一致（連続）を優先し、無ければ飛び石一致（順番どおりに全文字を拾えたときだけ）で印を付ける。
/// </summary>
internal static class PaletteHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(PaletteHighlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(PaletteHighlight), new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetQuery(DependencyObject o, string? v) => o.SetValue(QueryProperty, v);
    public static string? GetQuery(DependencyObject o) => (string?)o.GetValue(QueryProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;

        tb.Inlines.Clear();
        var text = GetText(tb);
        if (string.IsNullOrEmpty(text))
            return;

        var mask = PaletteHighlightPolicy.TitleMask(text, GetQuery(tb));
        var i = 0;
        while (i < text.Length)
        {
            var on = mask[i];
            var start = i;
            while (i < text.Length && mask[i] == on)
                i++;
            var run = new Run(text[start..i]);
            if (on)
            {
                // 印は「文字の地色＋太字」。文字色をアクセントにすると、アクセントで塗った選択行の上で
                // 同系色どうしになって消える——一番見ている行の一致だけ読めない、という裏返しになる。
                // SearchHighlight は選択行・通常行のどちらでも読めるよう作ってある半透明色（Palette.*）。
                run.SetResourceReference(TextElement.BackgroundProperty, "SearchHighlight");
                run.FontWeight = FontWeights.Bold;
            }
            // 印の無い文字は色を指定しない：行（ListBoxItem）の文字色をそのまま継ぐので、
            // 選択行では地色に載る色へ一緒に変わる。
            tb.Inlines.Add(run);
        }
    }

}

/// <summary>
/// パレットのプレビュー本文の1行を描く添付ビヘイビア。<b>構文色</b>（エディタと同じ字句解析の結果＝
/// <see cref="TokensProperty"/>）と<b>検索語の一致</b>（<see cref="TermProperty"/>）を1回で重ねる。
/// 一覧見出し用の <see cref="PaletteHighlight"/> と分けてあるのは、飛び石一致の印が本文の長い行に
/// 散らばると「どこが当たったのか」が読めなくなるため（本文はリテラル一致のみ・全出現を塗る）。
/// </summary>
internal static class PaletteMatchHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(PaletteMatchHighlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermProperty = DependencyProperty.RegisterAttached(
        "Term", typeof(string), typeof(PaletteMatchHighlight), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TokensProperty = DependencyProperty.RegisterAttached(
        "Tokens", typeof(IReadOnlyList<SyntaxToken>), typeof(PaletteMatchHighlight), new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetTerm(DependencyObject o, string? v) => o.SetValue(TermProperty, v);
    public static string? GetTerm(DependencyObject o) => (string?)o.GetValue(TermProperty);
    // 配列型のままだと XAML のテンプレート内で属性記法にできない（MC4102）ため IReadOnlyList で受ける。
    public static void SetTokens(DependencyObject o, IReadOnlyList<SyntaxToken>? v) => o.SetValue(TokensProperty, v);
    public static IReadOnlyList<SyntaxToken>? GetTokens(DependencyObject o) => (IReadOnlyList<SyntaxToken>?)o.GetValue(TokensProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;

        tb.Inlines.Clear();
        var text = GetText(tb);
        if (string.IsNullOrEmpty(text))
            return;

        // 色（構文）と印（検索語）は別の理由で変わるので、文字ごとに両方を決めてから、
        // 同じ組み合わせが続く区間をまとめて1つの Run にする（Run を1文字ずつ作らない）。
        var colors = Colors(text, GetTokens(tb));
        var hits = PaletteHighlightPolicy.LiteralMask(text, GetTerm(tb));

        var at = 0;
        while (at < text.Length)
        {
            var color = colors[at];
            var hit = hits[at];
            var start = at;
            while (at < text.Length && ReferenceEquals(colors[at], color) && hits[at] == hit)
                at++;
            var run = new Run(text[start..at]);
            if (color is not null)
                run.Foreground = color;
            else
                run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            if (hit)
                run.SetResourceReference(TextElement.BackgroundProperty, "SearchHighlight");
            tb.Inlines.Add(run);
        }
    }

    /// <summary>文字ごとの前景色。トークンの無い区間・色を持たない種別は null（＝テーマの <c>Fg</c>）。
    /// 重なりや逆順のトークンが来ても行の文字列を壊さないよう、常に「ここまで」以降だけを見る
    /// （差分本体の <c>SyntaxRuns</c> と同じ流儀）。</summary>
    private static Brush?[] Colors(string text, IReadOnlyList<SyntaxToken>? tokens)
    {
        var colors = new Brush?[text.Length];
        if (tokens is null)
            return colors;

        var position = 0;
        foreach (var token in tokens)
        {
            var start = Math.Clamp(token.StartColumn, position, text.Length);
            var end = Math.Clamp(start + token.Length, start, text.Length);
            if (end == start)
                continue;
            if (EditorSyntaxColors.Foreground(token.Kind) is { } brush)
                for (var i = start; i < end; i++)
                    colors[i] = brush;
            position = end;
        }
        return colors;
    }

}
