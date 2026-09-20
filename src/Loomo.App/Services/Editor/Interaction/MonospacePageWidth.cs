using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 等幅本文（差分本体・コンフリクトの Ours/Theirs）を折り返さずに出すための FlowDocument ページ幅を決める。
///
/// <para><b>全行を実測してはいけない。</b> 欲しいのは「最長行1本の幅」だけなのに、行ごとに
/// <see cref="FormattedText"/> を作ると行数に比例して重くなる——差分の左右並びは全文コンテキストで
/// 1ファイル分の行を持ち、しかも左右2列ぶん測るので、実測で2000行あたり約260ms、4000行で約380ms
/// UI スレッドを止めていた（グリフ整形のコストが行数×2回かかる）。</para>
///
/// <para>そこで<b>候補を絞ってから実測</b>する：安い尺度で最長候補を数本拾い、その候補にだけ
/// <see cref="FormattedText"/> を当てて最大値を採る。尺度はあくまで<b>候補の順位付け</b>にしか
/// 使わないので、幅そのものの精度は実測と変わらない（実測との一致は
/// <c>MonospacePageWidthTests</c> が全行実測と突き合わせて保証する）。</para>
///
/// <para><b>尺度は2つ要る。</b> 実測すると Cascadia Mono では ASCII が 7.03px なのに対し、
/// 全角かなは 9.79px（＝1.39倍であって2倍ではない）、罫線 <c>─</c> や矢印 <c>→</c> は 7.03px
/// （＝ASCII と同じ幅）。つまり「全角を2セルと数える」尺度は非 ASCII を<b>過大評価</b>する。
/// これ1本だけで順位を付けると、全角の行が候補枠を埋め尽くして<b>本当に一番長い ASCII 行が
/// 押し出され</b>、ページ幅が足りずに本文だけ折り返して行番号ガターと1行ずつずれる
/// （全角50字×20行＋ASCII90字、で 656.7px 必要なところを 445.8px と算出していた実バグ）。
/// そこでセル数（幅を食う文字が多い行を捕まえる）と文字数（重み無し。ASCII だけで長い行を
/// 捕まえる）の<b>両方</b>で候補を拾い、和集合を実測する。</para>
/// </summary>
internal static class MonospacePageWidth
{
    /// <summary>1つの尺度あたり実測にかける候補の本数（実測するのは2尺度の和集合なので最大その倍）。</summary>
    internal const int MeasuredCandidates = 16;

    /// <summary>本文右端の余白（横スクロールの行き過ぎ防止）。</summary>
    private const double PageWidthPadding = 24.0;

    /// <summary>計測できるものが無いときの最小ページ幅。</summary>
    internal const double MinWidth = 200.0;

    // 候補の順位付け専用の重み。実測（Cascadia Mono）ではタブが ASCII の約6.8倍、全角かなが約1.4倍、
    // 絵文字が約2.3倍。ここは「幅を食う行を取りこぼさない」ための<b>上振れ側</b>の見積りにしてあり、
    // 過大評価で ASCII の長行を押し出す分は、もう一方の尺度（文字数）が拾う。
    private const int TabCells = 8;
    private const int NonAsciiCells = 2;
    private const int AstralCells = 3; // 絵文字などのサロゲートペア

    /// <summary>
    /// <paramref name="lines"/> の最長行が収まるページ幅（px）。
    /// <paramref name="fontSize"/> は本文と同じ<b>スケール後</b>のサイズでなければならない——等倍で測ると
    /// UI 文字サイズを上げているときにページ幅が実際より狭くなり、本文だけが折り返して行番号とずれる。
    /// </summary>
    internal static double Measure(
        IEnumerable<string> lines, Typeface typeface, double fontSize, double pixelsPerDip)
    {
        var max = 0.0;
        foreach (var text in LongestCandidates(lines))
        {
            var formatted = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, pixelsPerDip);
            if (formatted.WidthIncludingTrailingWhitespace > max)
                max = formatted.WidthIncludingTrailingWhitespace;
        }
        return Math.Max(MinWidth, max + PageWidthPadding);
    }

    /// <summary>最長候補を返す（順序は問わない）。セル数と文字数の2つの尺度それぞれで上位
    /// <see cref="MeasuredCandidates"/> 本を拾い、その和集合。</summary>
    internal static List<string> LongestCandidates(IEnumerable<string> lines)
    {
        var byCells = new List<(int Key, string Text)>(MeasuredCandidates);
        var byLength = new List<(int Key, string Text)>(MeasuredCandidates);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            Offer(byCells, CellCount(line), line);
            Offer(byLength, CodePointCount(line), line);
        }

        var candidates = new List<string>(byCells.Count + byLength.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in byCells) if (seen.Add(text)) candidates.Add(text);
        foreach (var (_, text) in byLength) if (seen.Add(text)) candidates.Add(text);
        return candidates;
    }

    /// <summary>先頭を常に「拾った中で最も低い候補」に保つ小さなバッファへ差し出す
    /// （行数分の一覧を作らずに上位だけを残す）。</summary>
    private static void Offer(List<(int Key, string Text)> top, int key, string text)
    {
        if (top.Count < MeasuredCandidates)
            top.Add((key, text));
        else if (key > top[0].Key)
            top[0] = (key, text);
        else
            return;
        top.Sort(static (a, b) => a.Key.CompareTo(b.Key));
    }

    /// <summary>等幅表示での占有セル数の見積り（候補の順位付け専用。厳密な幅ではない）。</summary>
    internal static int CellCount(string text)
    {
        var cells = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\t') { cells += TabCells; continue; }
            if (IsAstralAt(text, i)) { cells += AstralCells; i++; continue; }
            cells += text[i] < 0x80 ? 1 : NonAsciiCells;
        }
        return cells;
    }

    /// <summary>コードポイント数（重み無しの尺度。ASCII だけで長い行を捕まえる側）。</summary>
    internal static int CodePointCount(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsAstralAt(text, i)) i++;
            count++;
        }
        return count;
    }

    private static bool IsAstralAt(string text, int index)
        => char.IsHighSurrogate(text[index])
           && index + 1 < text.Length
           && char.IsLowSurrogate(text[index + 1]);
}
