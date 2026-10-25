using System;
using System.Collections.Generic;
using System.Text;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>差分の行の側（移動の判定に使うもの）。</summary>
public enum DiffMoveSide { None, Removed, Added }

/// <summary>
/// 移動したブロックの 1 行の印。<see cref="Block"/> は同じブロックの行に共通の番号、<see cref="CounterpartRow"/> は相手側の
/// ブロックの先頭行、<see cref="IsSource"/> は移動元（削除側）か、<see cref="Length"/> はブロックの行数。
/// </summary>
public sealed record DiffMove(int Block, int CounterpartRow, bool IsSource, int Length);

/// <summary>
/// 差分の中で「削除と追加が、実は同じブロックの移動」であるものを見分ける（純ロジック・テスト対象）。§24.70。
/// <para>git の <c>--color-moved</c> と同じ考え方で、削除された行の並びと追加された行の並びが、空白を除いて
/// <see cref="MinLines"/> 行以上続けて一致するものを移動とする。メソッドの並べ替えや別の場所への抽出が「全部消して全部書いた」に
/// 見えて、本当の変更が埋もれるのを防ぐ。</para>
/// <para>移動とみなさないもの：同じ変更のかたまりの中での一致（その場で字下げを直しただけ——それは §24.25 の空白無視の仕事）、
/// 中身の薄いブロック（<c>}</c> や空行の並び——<see cref="MinChars"/> 字に満たない）。</para>
/// </summary>
public static class DiffMoveDetector
{
    /// <summary>移動とみなす最小の行数。</summary>
    public const int MinLines = 3;

    /// <summary>移動とみなす最小の中身（空白を除いた文字数の合計）。<c>}</c>・<c>});</c>・空行だけの並びを拾わない。</summary>
    public const int MinChars = 30;

    /// <summary>これを超える行数の差分は調べない（構文色・行内差分と同じ頭打ち）。</summary>
    public const int MaxRows = 20_000;

    /// <summary>1 行あたりに試す相手の数（同じ行が大量にある差分で遅くならないように）。</summary>
    private const int MaxCandidates = 64;

    /// <summary>行と 1 対 1 の印（移動でない行は null）。<paramref name="rows"/> は各行の側と本文（パッチの 1 文字は剥がしたもの）。</summary>
    public static IReadOnlyList<DiffMove?> Detect(IReadOnlyList<(DiffMoveSide Side, string Body)> rows)
    {
        var result = new DiffMove?[rows.Count];
        if (rows.Count == 0 || rows.Count > MaxRows)
            return result;

        var norm = new string[rows.Count];
        var chunk = new int[rows.Count];
        var current = -1;
        var inChunk = false;
        for (var i = 0; i < rows.Count; i++)
        {
            norm[i] = Normalize(rows[i].Body);
            if (rows[i].Side == DiffMoveSide.None)
            {
                inChunk = false;
                chunk[i] = -1;
                continue;
            }
            if (!inChunk)
            {
                current++;
                inChunk = true;
            }
            chunk[i] = current;
        }

        var addedByText = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Side != DiffMoveSide.Added || norm[i].Length == 0)
                continue;
            if (!addedByText.TryGetValue(norm[i], out var list))
                addedByText[norm[i]] = list = new List<int>();
            if (list.Count < MaxCandidates)
                list.Add(i);
        }

        var block = 0;
        var i0 = 0;
        while (i0 < rows.Count)
        {
            if (rows[i0].Side != DiffMoveSide.Removed || result[i0] is not null || norm[i0].Length == 0
                || !addedByText.TryGetValue(norm[i0], out var candidates))
            {
                i0++;
                continue;
            }
            var (bestStart, bestLength, bestChars) = (-1, 0, 0);
            foreach (var j in candidates)
            {
                if (result[j] is not null || chunk[j] == chunk[i0])
                    continue;
                var (length, chars) = Extend(rows, norm, result, i0, j);
                if (length > bestLength || (length == bestLength && bestStart >= 0 && Math.Abs(j - i0) < Math.Abs(bestStart - i0)))
                    (bestStart, bestLength, bestChars) = (j, length, chars);
            }
            if (bestLength >= MinLines && bestChars >= MinChars)
            {
                for (var k = 0; k < bestLength; k++)
                {
                    result[i0 + k] = new DiffMove(block, bestStart, IsSource: true, bestLength);
                    result[bestStart + k] = new DiffMove(block, i0, IsSource: false, bestLength);
                }
                block++;
                i0 += bestLength;
            }
            else
                i0++;
        }
        return result;
    }

    /// <summary>削除行 <paramref name="i"/> と追加行 <paramref name="j"/> から、両方が続く限り同じ本文の行数と中身の文字数。</summary>
    private static (int Length, int Chars) Extend(
        IReadOnlyList<(DiffMoveSide Side, string Body)> rows, string[] norm, DiffMove?[] used, int i, int j)
    {
        var length = 0;
        var chars = 0;
        while (i + length < rows.Count && j + length < rows.Count
               && rows[i + length].Side == DiffMoveSide.Removed && rows[j + length].Side == DiffMoveSide.Added
               && used[i + length] is null && used[j + length] is null
               && string.Equals(norm[i + length], norm[j + length], StringComparison.Ordinal)
               && i + length != j)
        {
            chars += norm[i + length].Length;
            length++;
        }
        // 末尾の空行は数えない（空行の続きで次のブロックとつながって見えないように）。
        while (length > 0 && norm[i + length - 1].Length == 0)
            length--;
        return (length, chars);
    }

    /// <summary>空白を全部除いた本文（字下げ・行末の空白・改行の違いを同じに見る）。</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        return sb.ToString();
    }
}
