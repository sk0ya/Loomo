using System.Collections.Generic;
using System.Text.RegularExpressions;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 差分本体の表示行 → 実ファイルの行番号（分からなければ 0）。
/// Diff ペインで右クリックした行をエディタで開くために使う。
///
/// <b>どちら側を読むかは呼び出し側が渡す</b>：git 差分・AI変更は右（新側）がその実ファイルだが、
/// アドホック比較では「ファイル ↔ クリップボード」のようにファイルが左に来ることがある
/// （<see cref="DiffFileItem.FileIsLeft"/>）。取り違えると反対側の行番号へ飛ぶ。
///
/// 左右並びは各行が両側の行番号を持っているのでそれを読むだけ。統合表示は2種類あり、どちらも数える：
/// git のパッチは <c>@@ -a,b +c,d @@</c> で両側の開始行が分かり、その側の行（追加なら新側、削除なら旧側、
/// 文脈行は両側）が1行ずつ消費する。AI変更／アドホック比較は <see cref="DiffUtil.Compute"/> の出力で、
/// @@ の代わりに「… N 行省略 …」の Gap が入る（畳まれるのは文脈行だけなので、N は両側とも N 行ぶん）。
/// </summary>
public static class DiffRowLineMapper
{
    private static readonly Regex SkippedLines = new(@"(\d+)\s*行省略", RegexOptions.Compiled);

    /// <summary>左右並びの行 → その側の行番号。その側に無い行（反対側だけの変更）は直前の行を指す。</summary>
    public static int LineForSideRow(IReadOnlyList<DiffSideRowVm> rows, int index, bool leftSide)
    {
        if (index < 0 || index >= rows.Count) return 0;
        for (var i = index; i >= 0; i--)
            if (int.TryParse(leftSide ? rows[i].LeftLine : rows[i].RightLine, out var line))
                return line;
        return 0;
    }

    /// <summary>統合表示の行 → その側の行番号。その側に無い行・ヘッダ行は直前の行を指す。</summary>
    public static int LineForUnifiedRow(IReadOnlyList<DiffRowVm> rows, int index, bool leftSide)
    {
        if (index < 0 || index >= rows.Count) return 0;

        // git パッチは @@ が来るまで行番号が決まらない。全文差分（@@ なし）は先頭を 1 行目として数える。
        var counter = HasHunkHeader(rows) ? 0 : 1;
        var last = 0;
        // その側が消費する行：文脈行は常に、あとは左なら削除行・右なら追加行。
        var sideOnly = leftSide ? nameof(DiffLineKind.Removed) : nameof(DiffLineKind.Added);
        for (var i = 0; i <= index; i++)
        {
            var row = rows[i];
            if (row.Kind == nameof(DiffLineKind.Gap))
            {
                if (SideBySideDiff.TryParseHunkStarts(row.Text, out var oldStart, out var newStart))
                    counter = leftSide ? oldStart : newStart;   // git のハンク見出し
                else if (SkippedLines.Match(row.Text) is { Success: true } m)
                    counter += int.Parse(m.Groups[1].Value);    // 畳まれた文脈行（両側とも同じ行数進む）
                continue;
            }
            if (counter <= 0 || (row.Kind != nameof(DiffLineKind.Context) && row.Kind != sideOnly))
                continue;
            last = counter;
            counter++;
        }
        return last;
    }

    private static bool HasHunkHeader(IReadOnlyList<DiffRowVm> rows)
    {
        foreach (var row in rows)
            if (row.Kind == nameof(DiffLineKind.Gap)
                && SideBySideDiff.TryParseHunkStarts(row.Text, out _, out _))
                return true;
        return false;
    }
}
