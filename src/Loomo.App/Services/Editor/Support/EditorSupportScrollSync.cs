namespace sk0ya.Loomo.App.Services;

/// <summary>
/// エディタとプレビューのスクロールを<b>ソース行</b>で合わせるための換算。以前は双方の
/// スクロール比率で合わせていたが、画像・mermaid 図・表・長いコードブロックのように描画の高さが
/// ソースの行数と比例しない箇所があると、比率が同じでも見えている場所がずれていった。
/// 行の目印（プレビュー側はブロックの <c>data-source-line</c>）で合わせ、目印の間は線形に補う。
/// </summary>
/// <remarks>
/// エディタの表示行はバッファ行と一致しない（折り返し・折りたたみ・CodeLens の注釈行）。
/// <c>visualLineOf</c> はバッファ行（0始まり）→その本文が載る表示行で、畳まれていれば -1
/// （<c>VimEditorControl.GetTextVisualLine</c>）。これは表示行を頭から舐める O(表示行数) なので、
/// 呼ぶ回数は O(log² 行数) に抑える（畳まれた範囲を1行ずつ遡ると、大きな折りたたみで UI が止まる）。
/// <para>
/// 対応の単位は「見えている行 L から次に見えている行 N の手前まで」。ソースの [L, N) が表示行の
/// [visual(L), visual(N)) に線形に載る。折り返しは1行が複数表示行へ、折りたたみは複数行が見出しの
/// 表示行へ載る形で、どちらも同じ式で扱える。
/// </para>
/// </remarks>
internal static class EditorSupportScrollSync
{
    /// <summary>表示行（小数＝行内のずれ込み）の位置にあるソース行（小数）。</summary>
    public static double SourceLineAt(
        double visualLine, int lineCount, int visualLineCount, Func<int, int> visualLineOf)
    {
        if (lineCount <= 0 || visualLine <= 0)
            return 0;
        // 手前の見えている行の表示行が visualLine 以下になる最大の b（b について単調）。
        int lo = 0, hi = lineCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var (_, v) = PreviousVisible(mid, visualLineOf);
            if (v >= 0 && v <= visualLine) lo = mid;
            else hi = mid - 1;
        }
        var (line, start) = PreviousVisible(lo, visualLineOf);
        if (start < 0)
            return 0;
        var (next, end) = NextVisible(line, start, lineCount, visualLineCount, visualLineOf);
        return line + Math.Clamp((visualLine - start) / (end - start), 0, 1) * (next - line);
    }

    /// <summary>ソース行（小数）の上端が載る表示行（小数）。<see cref="SourceLineAt"/> の逆。</summary>
    public static double VisualLineAt(
        double sourceLine, int lineCount, int visualLineCount, Func<int, int> visualLineOf)
    {
        if (lineCount <= 0 || sourceLine <= 0)
            return 0;
        var (line, start) = PreviousVisible(Math.Min((int)Math.Floor(sourceLine), lineCount - 1), visualLineOf);
        if (start < 0)
            return 0;
        var (next, end) = NextVisible(line, start, lineCount, visualLineCount, visualLineOf);
        return start + Math.Clamp((sourceLine - line) / (next - line), 0, 1) * (end - start);
    }

    /// <summary>
    /// 末尾の1画面ぶんでは、行の対応から相手側の終端へ滑らかに寄せる割合（0〜1）。
    /// 片方が最後までスクロールし切っても、行で合わせただけでは相手の終端に届かない（見える
    /// 範囲の高さが違う）ので、その残りを最後の1画面で埋める。
    /// </summary>
    public static double TailBlend(double offset, double maxOffset, double viewport)
    {
        if (maxOffset <= 0 || viewport <= 0)
            return 0;
        var remaining = maxOffset - offset;
        return remaining >= viewport ? 0 : Math.Clamp(1 - remaining / viewport, 0, 1);
    }

    // b 以前で最も近い見えている行とその表示行。畳まれた範囲は倍々に遡ってから二分探索する。
    // 見つからなければ (-1, -1)。
    private static (int Line, int Visual) PreviousVisible(int b, Func<int, int> visualLineOf)
    {
        var v = visualLineOf(b);
        if (v >= 0)
            return (b, v);
        int folded = b, step = 1;
        while (true)
        {
            var probe = b - step;
            if (probe < 0)
            {
                if ((v = visualLineOf(0)) < 0)
                    return (-1, -1);
                return Narrow(0, v, folded, visualLineOf);
            }
            if ((v = visualLineOf(probe)) >= 0)
                return Narrow(probe, v, folded, visualLineOf);
            folded = probe;
            step *= 2;
        }
    }

    // 見えている visible と畳まれた folded（visible < folded）の境目にある、最後の見えている行。
    private static (int Line, int Visual) Narrow(int visible, int visibleVisual, int folded, Func<int, int> visualLineOf)
    {
        while (folded - visible > 1)
        {
            var mid = (visible + folded) / 2;
            var v = visualLineOf(mid);
            if (v >= 0) { visible = mid; visibleVisual = v; }
            else folded = mid;
        }
        return (visible, visibleVisual);
    }

    // line の次に見えている行と、その表示行（＝line の表示上の終わり）。末尾なら (行数, 表示行数)。
    // 間に注釈行が挟まっていても、その分は line に含めて補う。
    private static (int Line, int Visual) NextVisible(
        int line, int start, int lineCount, int visualLineCount, Func<int, int> visualLineOf)
    {
        int folded = line, step = 1, next = -1, nextVisual = -1;
        while (true)
        {
            var probe = line + step;
            if (probe >= lineCount)
            {
                // 末尾まで畳まれているか確かめ、そうでなければ境目を探す。
                var last = lineCount - 1;
                if (last <= folded || visualLineOf(last) < 0)
                    return (lineCount, Math.Max(visualLineCount, start + 1));
                probe = last;
            }
            var v = visualLineOf(probe);
            if (v >= 0) { next = probe; nextVisual = v; break; }
            folded = probe;
            step *= 2;
        }
        // folded（畳まれている・または line 自身）と next（見えている）の間で、最初の見えている行。
        while (next - folded > 1)
        {
            var mid = (folded + next) / 2;
            var v = visualLineOf(mid);
            if (v >= 0) { next = mid; nextVisual = v; }
            else folded = mid;
        }
        return (next, nextVisual > start ? nextVisual : start + 1);
    }
}
