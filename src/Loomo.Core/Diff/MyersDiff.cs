using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>編集操作の種別。</summary>
public enum EditKind
{
    Equal,   // 両側に同じ要素がある
    Delete,  // 旧側（a）にだけある
    Insert   // 新側（b）にだけある
}

/// <summary>
/// 編集操作1つ。<paramref name="A"/> は旧側の添字（Insert では -1）、<paramref name="B"/> は新側の添字
/// （Delete では -1）。
/// </summary>
public readonly record struct Edit(EditKind Kind, int A, int B);

/// <summary>
/// 整数列どうしの差分（Myers の O(ND) 法・線形空間の二分版）。行・字句など何の差分にも使えるよう、
/// 要素は呼び元が整数へ写したものを受け取る（同じ値＝同じ要素）。UI 非依存。
///
/// <para>以前の行差分は LCS の全表（n×m の int 表）を作っていて、3000 行どうしで約 36MB、5000 行を
/// 超えると「全部削除して全部追加」へ落ちていた。こちらは作業領域が O(n+m)、時間が O((n+m)·D)
/// （D＝違いの量）なので、ほとんど同じ2つの大きなファイル（編集中の差分の取り直しがまさにこれ）は
/// ほぼ線形で済む。</para>
///
/// <para>違いが極端に多い組み合わせでは、<paramref name="maxCost"/> を超えて探さずに「ここまでで
/// いちばん遠くまで進んだ点」で分割する（git の xdiff と同じ打ち切り方）。結果は最短ではなくなるが、
/// 編集操作として正しいことは変わらない。</para>
/// </summary>
public static class MyersDiff
{
    /// <summary>1回の二分探索で調べる違いの量の上限。超えたら近似の分割点で切る。</summary>
    public const int DefaultMaxCost = 2048;

    /// <summary>
    /// <paramref name="a"/> → <paramref name="b"/> の編集操作を、両側の順序どおりに返す。
    /// 連続する Delete/Insert の並び順（交互に出るか）は保証しない——揃えたいなら呼び元で並べ替える。
    /// </summary>
    public static List<Edit> Diff(int[] a, int[] b, int maxCost = DefaultMaxCost)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (maxCost < 1) maxCost = 1;

        var edits = new List<Edit>(Math.Max(a.Length, b.Length));
        // 再帰の代わりに明示的なスタック（大きな入力で再帰が深くなっても落ちないように）。
        // 後に出す仕事から積む：共通の末尾 → 右半分 → 左半分 の順に取り出したいので逆順に積む。
        var stack = new Stack<WorkItem>();
        stack.Push(WorkItem.Range(0, a.Length, 0, b.Length));
        while (stack.Count > 0)
        {
            var task = stack.Pop();
            if (task.IsEqualRun)
            {
                for (var i = 0; i < task.Length; i++)
                    edits.Add(new Edit(EditKind.Equal, task.ALo + i, task.BLo + i));
                continue;
            }

            var (aLo, aHi, bLo, bHi) = (task.ALo, task.AHi, task.BLo, task.BHi);

            // 共通の先頭
            var prefix = 0;
            while (aLo + prefix < aHi && bLo + prefix < bHi && a[aLo + prefix] == b[bLo + prefix])
                prefix++;
            for (var i = 0; i < prefix; i++)
                edits.Add(new Edit(EditKind.Equal, aLo + i, bLo + i));
            aLo += prefix;
            bLo += prefix;

            // 共通の末尾（中身を出したあとで出す）
            var suffix = 0;
            while (aHi - suffix > aLo && bHi - suffix > bLo && a[aHi - suffix - 1] == b[bHi - suffix - 1])
                suffix++;
            aHi -= suffix;
            bHi -= suffix;
            if (suffix > 0)
                stack.Push(WorkItem.EqualRun(aHi, bHi, suffix));

            if (aLo == aHi)
            {
                for (var j = bLo; j < bHi; j++) edits.Add(new Edit(EditKind.Insert, -1, j));
                continue;
            }
            if (bLo == bHi)
            {
                for (var i = aLo; i < aHi; i++) edits.Add(new Edit(EditKind.Delete, i, -1));
                continue;
            }

            if (Bisect(a, aLo, aHi, b, bLo, bHi, maxCost, out var x, out var y))
            {
                stack.Push(WorkItem.Range(aLo + x, aHi, bLo + y, bHi));
                stack.Push(WorkItem.Range(aLo, aLo + x, bLo, bLo + y));
            }
            else
            {
                // 共通部分が1つも無い：全部削除して全部追加
                for (var i = aLo; i < aHi; i++) edits.Add(new Edit(EditKind.Delete, i, -1));
                for (var j = bLo; j < bHi; j++) edits.Add(new Edit(EditKind.Insert, -1, j));
            }
        }
        return edits;
    }

    /// <summary>
    /// 中央の「蛇」（最短経路の途中の点）を前後両方から探して、分割点 (x, y)（範囲の先頭からの相対）を返す。
    /// 範囲は先頭・末尾の共通部分を剥がし済み・両側とも空でないこと。分割点は必ず
    /// (0,0) と (n,m) 以外になる（分割で問題が必ず小さくなる）。
    /// </summary>
    private static bool Bisect(
        int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, int maxCost, out int splitX, out int splitY)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;
        var maxD = (n + m + 1) / 2;
        var vOffset = maxD;
        var vLength = 2 * maxD + 2;
        var v1 = new int[vLength];
        var v2 = new int[vLength];
        Array.Fill(v1, -1);
        Array.Fill(v2, -1);
        v1[vOffset + 1] = 0;
        v2[vOffset + 1] = 0;
        var delta = n - m;
        // 総ステップ数が奇数なら前進側で、偶数なら後退側で重なりを調べる。
        var front = (delta & 1) != 0;
        int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;

        for (var d = 0; d < maxD; d++)
        {
            if (d > maxCost)
                return ApproximateSplit(v1, vOffset, d, k1Start, k1End, n, m, out splitX, out splitY);

            for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
            {
                var k1Offset = vOffset + k1;
                int x1 = k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1])
                    ? v1[k1Offset + 1]
                    : v1[k1Offset - 1] + 1;
                var y1 = x1 - k1;
                while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                {
                    x1++;
                    y1++;
                }
                v1[k1Offset] = x1;
                if (x1 > n)
                    k1End += 2;     // 右端からはみ出した
                else if (y1 > m)
                    k1Start += 2;   // 下端からはみ出した
                else if (front)
                {
                    var k2Offset = vOffset + delta - k1;
                    if (k2Offset >= 0 && k2Offset < vLength && v2[k2Offset] != -1)
                    {
                        var x2 = n - v2[k2Offset];
                        if (x1 >= x2)
                        {
                            splitX = x1;
                            splitY = y1;
                            return true;
                        }
                    }
                }
            }

            for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
            {
                var k2Offset = vOffset + k2;
                int x2 = k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1])
                    ? v2[k2Offset + 1]
                    : v2[k2Offset - 1] + 1;
                var y2 = x2 - k2;
                while (x2 < n && y2 < m && a[aHi - x2 - 1] == b[bHi - y2 - 1])
                {
                    x2++;
                    y2++;
                }
                v2[k2Offset] = x2;
                if (x2 > n)
                    k2End += 2;
                else if (y2 > m)
                    k2Start += 2;
                else if (!front)
                {
                    var k1Offset = vOffset + delta - k2;
                    if (k1Offset >= 0 && k1Offset < vLength && v1[k1Offset] != -1)
                    {
                        var x1 = v1[k1Offset];
                        var y1 = vOffset + x1 - k1Offset;
                        if (x1 >= n - x2)
                        {
                            splitX = x1;
                            splitY = y1;
                            return true;
                        }
                    }
                }
            }
        }

        splitX = splitY = 0;
        return false;
    }

    /// <summary>
    /// 打ち切り時の分割点：前進側でいちばん遠く（x+y が最大）まで進んだ対角線の端。d ≥ 1 なので
    /// (0,0) ではなく、まだ重なっていないので (n,m) でもない——必ず問題が小さくなる。
    /// </summary>
    private static bool ApproximateSplit(
        int[] v1, int vOffset, int d, int k1Start, int k1End, int n, int m, out int splitX, out int splitY)
    {
        var bestX = -1;
        var bestY = -1;
        // 直前の周回（d-1）で書いた対角線だけを見る。
        var last = d - 1;
        for (var k = -last + k1Start; k <= last - k1End; k += 2)
        {
            var x = v1[vOffset + k];
            if (x < 0) continue;
            var y = x - k;
            if (x > n || y > m || y < 0) continue;
            if (x + y > bestX + bestY)
            {
                bestX = x;
                bestY = y;
            }
        }
        if (bestX < 0 || bestX + bestY == 0 || (bestX == n && bestY == m))
        {
            splitX = splitY = 0;
            return false;
        }
        splitX = bestX;
        splitY = bestY;
        return true;
    }

    private readonly record struct WorkItem(int ALo, int AHi, int BLo, int BHi, bool IsEqualRun, int Length)
    {
        public static WorkItem Range(int aLo, int aHi, int bLo, int bHi) => new(aLo, aHi, bLo, bHi, false, 0);
        public static WorkItem EqualRun(int aLo, int bLo, int length) => new(aLo, 0, bLo, 0, true, length);
    }
}
