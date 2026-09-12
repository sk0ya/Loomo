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
/// コマンドパレット一覧のタイトルを、現在のクエリに一致した文字だけ強調（Accent＋太字）して描画する
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

        var mask = ComputeMask(text, GetQuery(tb));
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
                run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                run.FontWeight = FontWeights.Bold;
            }
            else
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            }
            tb.Inlines.Add(run);
        }
    }

    /// <summary>タイトル各文字を強調するか否かのマスク。空白区切りの各語について、まず連続一致（部分一致）を、
    /// 無ければ飛び石一致（全文字を順番どおり拾えたときだけ）で印を付ける。</summary>
    private static bool[] ComputeMask(string title, string? query)
    {
        var mask = new bool[title.Length];
        if (string.IsNullOrWhiteSpace(query))
            return mask;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var idx = title.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                for (var k = 0; k < token.Length; k++)
                    mask[idx + k] = true;
                continue;
            }
            MarkSubsequence(title, token, mask);
        }
        return mask;
    }

    /// <summary>飛び石一致：token の全文字を title 内で順番どおり拾えたときだけ、その位置に印を付ける
    /// （途中までしか拾えない語は誤ハイライトを避けて何も付けない）。</summary>
    private static void MarkSubsequence(string title, string token, bool[] mask)
    {
        var hit = new List<int>(token.Length);
        var n = 0;
        for (var i = 0; i < title.Length && n < token.Length; i++)
        {
            if (char.ToUpperInvariant(title[i]) == char.ToUpperInvariant(token[n]))
            {
                hit.Add(i);
                n++;
            }
        }
        if (n == token.Length)
            foreach (var i in hit)
                mask[i] = true;
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
        var hits = Hits(text, GetTerm(tb));

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

    /// <summary>文字ごとの「検索語に当たったか」。リテラル一致の全出現（大文字小文字は無視）。</summary>
    private static bool[] Hits(string text, string? term)
    {
        var hits = new bool[text.Length];
        if (string.IsNullOrEmpty(term))
            return hits;

        var at = 0;
        while (at < text.Length)
        {
            var found = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                break;
            for (var i = found; i < found + term.Length && i < text.Length; i++)
                hits[i] = true;
            at = found + term.Length;
        }
        return hits;
    }
}
