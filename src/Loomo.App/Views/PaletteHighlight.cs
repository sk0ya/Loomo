using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

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
