using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>
/// 行差分の「どこで区切るか」の曖昧さを、人が読みやすい位置へ寄せる（git の xdiff の
/// change compaction と同じ発想）。
///
/// <para>差分の最短性だけでは、追加・削除のかたまりをどこに置くかは決まらない。たとえば関数を1つ足すと、
/// 「<c>B() {</c> から空行まで」でも「前の関数の <c>}</c> から <c>B() {</c> まで」でも同じ長さの差分になる。
/// 素の最短経路は後者を選びがちで、前の関数の閉じ括弧が「消えて生えた」ように見える。</para>
///
/// <para>ここでは<b>片側だけの変更（純粋な追加／削除）のかたまり</b>だけを、前後の同じ行を跨いで上下へ
/// 滑らせ、候補の中から「空行の直後に始まる・空でない行から始まる・空行で終わる」位置を選ぶ。
/// 同点なら一番下（git の既定と同じ）。削除と追加が混ざったかたまり（書き換え）は動かさない——
/// 動かすと左右の行の組み合わせが変わってしまうため。かたまりどうしがくっつく位置へも動かさない。</para>
/// </summary>
internal static class DiffCompaction
{
    /// <summary>
    /// <paramref name="edits"/> をその場で書き換える。<paramref name="edits"/> は各変更のかたまりが
    /// 「Delete 群 → Insert 群」の順に並んでいること（<see cref="DiffUtil"/> が整えてから渡す）。
    /// </summary>
    public static void Compact(List<Edit> edits, int[] aKeys, int[] bKeys, bool[] aBlank, bool[] bBlank)
    {
        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Equal)
            {
                i++;
                continue;
            }
            var start = i;
            var kind = edits[i].Kind;
            var pure = true;
            while (i < edits.Count && edits[i].Kind != EditKind.Equal)
            {
                if (edits[i].Kind != kind) pure = false;
                i++;
            }
            if (!pure) continue;

            var end = CompactGroup(edits, start, i, kind == EditKind.Insert,
                kind == EditKind.Insert ? bKeys : aKeys,
                kind == EditKind.Insert ? bBlank : aBlank);
            i = end;
        }
    }

    /// <summary>1つの純粋なかたまり [start, end) を最良の位置へ動かし、動かした後の終端を返す。</summary>
    private static int CompactGroup(List<Edit> edits, int start, int end, bool insert, int[] keys, bool[] blank)
    {
        // いったん上へ滑らせきってから、下へ1つずつ滑らせながら位置ごとの点数を測る。
        while (SlideUp(edits, start, end, insert, keys))
        {
            start--;
            end--;
        }
        var bestOffset = 0;
        var bestScore = Score(edits, start, end, insert, blank);
        var offset = 0;
        while (SlideDown(edits, start, end, insert, keys))
        {
            start++;
            end++;
            offset++;
            var score = Score(edits, start, end, insert, blank);
            if (score >= bestScore)   // 同点なら下（後ろ）を選ぶ
            {
                bestScore = score;
                bestOffset = offset;
            }
        }
        for (; offset > bestOffset; offset--)
        {
            SlideUp(edits, start, end, insert, keys);
            start--;
            end--;
        }
        return end;
    }

    private static int Line(Edit edit, bool insert) => insert ? edit.B : edit.A;

    /// <summary>
    /// かたまりを1行上へ：直前の一致行とかたまりの最後の行が同じなら、その一致行をかたまりへ取り込み、
    /// かたまりの最後の行を一致行にする。直前の一致行のさらに前が変更なら（くっつくので）動かさない。
    /// </summary>
    private static bool SlideUp(List<Edit> edits, int start, int end, bool insert, int[] keys)
    {
        if (start < 1 || edits[start - 1].Kind != EditKind.Equal) return false;
        if (start >= 2 && edits[start - 2].Kind != EditKind.Equal) return false;
        var equal = edits[start - 1];
        var before = Line(equal, insert);
        var last = Line(edits[end - 1], insert);
        if (keys[before] != keys[last]) return false;

        var kind = insert ? EditKind.Insert : EditKind.Delete;
        for (var k = start - 1; k < end - 1; k++)
        {
            var line = before + (k - (start - 1));
            edits[k] = insert ? new Edit(kind, -1, line) : new Edit(kind, line, -1);
        }
        edits[end - 1] = insert
            ? new Edit(EditKind.Equal, equal.A, last)
            : new Edit(EditKind.Equal, last, equal.B);
        return true;
    }

    /// <summary>かたまりを1行下へ（<see cref="SlideUp"/> の逆）。</summary>
    private static bool SlideDown(List<Edit> edits, int start, int end, bool insert, int[] keys)
    {
        if (end >= edits.Count || edits[end].Kind != EditKind.Equal) return false;
        if (end + 1 < edits.Count && edits[end + 1].Kind != EditKind.Equal) return false;
        var equal = edits[end];
        var after = Line(equal, insert);
        var first = Line(edits[start], insert);
        if (keys[after] != keys[first]) return false;

        var kind = insert ? EditKind.Insert : EditKind.Delete;
        edits[start] = insert
            ? new Edit(EditKind.Equal, equal.A, first)
            : new Edit(EditKind.Equal, first, equal.B);
        for (var k = start + 1; k <= end; k++)
        {
            var line = first + (k - start);
            edits[k] = insert ? new Edit(kind, -1, line) : new Edit(kind, line, -1);
        }
        return true;
    }

    /// <summary>読みやすさの点数：空行（またはファイル先頭）の直後に始まる＋2、空でない行で始まる＋1、空行で終わる＋1。</summary>
    private static int Score(List<Edit> edits, int start, int end, bool insert, bool[] blank)
    {
        var first = Line(edits[start], insert);
        var last = Line(edits[end - 1], insert);
        var score = 0;
        if (first == 0 || blank[first - 1]) score += 2;
        if (!blank[first]) score += 1;
        if (blank[last]) score += 1;
        return score;
    }
}
