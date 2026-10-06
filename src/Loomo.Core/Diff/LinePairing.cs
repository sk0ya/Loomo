using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>
/// 削除と追加が続くかたまり（書き換え）で、どの削除行とどの追加行を左右の同じ高さに並べるかを決める。UI 非依存。
///
/// <para>以前は「i 番目の削除行と i 番目の追加行」を機械的に並べていた。1行足して1行直しただけで、
/// 直した行が隣の無関係な行と並び、似ている行どうしは段違いになっていた——行内差分（<see cref="InlineDiff"/>）は
/// 横に並んだ2行を比べるので、組み合わせを誤るとそもそも意味のある色にならない。</para>
///
/// <para>ここでは<b>似ている行どうし</b>（<see cref="InlineDiff.MinSimilarity"/> 以上）を、順序を保ったまま
/// 似かたの合計が最大になるように対にする（行数×行数の動的計画法）。対にならなかった行は、対と対の間で
/// 従来どおり上から詰めて並べる——似ていない行どうしが横に並ぶのは「置き換わった」だけで、行内差分は出ない。
/// かたまりが大きすぎるとき（<see cref="MaxCells"/> 超）は従来の並べ方に戻す。</para>
/// </summary>
public static class LinePairing
{
    /// <summary>削除行数×追加行数がこれを超えるかたまりは、似かたを測らずに上から詰めて並べる。</summary>
    public const int MaxCells = 10_000;

    /// <summary>
    /// 左右の並びを返す。各要素は (削除行の添字, 追加行の添字) で、その側に行が無ければ -1。
    /// 削除行・追加行はどちらも元の順序で1回ずつ現れる。
    /// </summary>
    public static List<(int Removed, int Added)> Pair(IReadOnlyList<string> removed, IReadOnlyList<string> added)
    {
        var rows = new List<(int, int)>(Math.Max(removed.Count, added.Count));
        var r = removed.Count;
        var a = added.Count;
        if (r == 0 || a == 0 || (long)r * a > MaxCells)
        {
            Zip(rows, 0, r, 0, a);
            return rows;
        }
        // どちらも1行なら（いちばん多い形）似かたに関わらず並べる——並べ方の選択肢が無い。
        if (r == 1 && a == 1)
        {
            rows.Add((0, 0));
            return rows;
        }

        var similarity = new double[r, a];
        for (var i = 0; i < r; i++)
            for (var j = 0; j < a; j++)
                similarity[i, j] = InlineDiff.Similarity(removed[i], added[j]);

        // best[i, j] = removed[i..] と added[j..] で取れる似かたの合計の最大
        var best = new double[r + 1, a + 1];
        for (var i = r - 1; i >= 0; i--)
        {
            for (var j = a - 1; j >= 0; j--)
            {
                var value = Math.Max(best[i + 1, j], best[i, j + 1]);
                if (similarity[i, j] >= InlineDiff.MinSimilarity)
                    value = Math.Max(value, best[i + 1, j + 1] + similarity[i, j]);
                best[i, j] = value;
            }
        }

        // 対を順に拾い、対と対の間は上から詰める。
        int x = 0, y = 0, fromX = 0, fromY = 0;
        while (x < r && y < a)
        {
            var sim = similarity[x, y];
            if (sim >= InlineDiff.MinSimilarity && Same(best[x, y], best[x + 1, y + 1] + sim))
            {
                Zip(rows, fromX, x, fromY, y);
                rows.Add((x, y));
                x++;
                y++;
                fromX = x;
                fromY = y;
            }
            else if (Same(best[x, y], best[x + 1, y]))
                x++;
            else
                y++;
        }
        Zip(rows, fromX, r, fromY, a);
        return rows;
    }

    private static bool Same(double left, double right) => Math.Abs(left - right) < 1e-9;

    private static void Zip(List<(int, int)> rows, int rFrom, int rTo, int aFrom, int aTo)
    {
        var count = Math.Max(rTo - rFrom, aTo - aFrom);
        for (var k = 0; k < count; k++)
            rows.Add((rFrom + k < rTo ? rFrom + k : -1, aFrom + k < aTo ? aFrom + k : -1));
    }
}
