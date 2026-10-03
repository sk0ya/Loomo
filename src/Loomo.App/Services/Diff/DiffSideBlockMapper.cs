using sk0ya.Loomo.App.ViewModels;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <param name="Stage">ブロックの変更がどこまでステージ済みか。ステージ済みと未ステージが隣り合っていれば
/// 別のブロックに分ける（帯の印とクリックの向きがブロックごとに1つに決まるように）。</param>
internal readonly record struct DiffSideBlock(
    int Start, int End, bool HasAdd, bool HasRemove, DiffStageState Stage = DiffStageState.None);

/// <summary>エディタで選んだ範囲の変更行（削除＝旧側の行番号、追加＝新側の行番号）と、その中のステージの状況。</summary>
internal sealed record DiffSideSelection(
    IReadOnlySet<int> OldLines, IReadOnlySet<int> NewLines, bool HasStaged, bool HasUnstaged)
{
    internal static DiffSideSelection Empty { get; } = new(new HashSet<int>(), new HashSet<int>(), false, false);
    internal bool IsEmpty => OldLines.Count == 0 && NewLines.Count == 0;
}
internal readonly record struct DiffSideBlockPlacement(double Top, double Height);

/// <summary>左右差分行から、連続した変更行のブロックを組み立てる。</summary>
internal static class DiffSideBlockMapper
{
    internal static bool TryGetVisiblePlacement(
        DiffSideBlock block, double lineHeight, double verticalOffset, double viewportHeight,
        out DiffSideBlockPlacement placement)
    {
        var top = block.Start * lineHeight - verticalOffset;
        var height = (block.End - block.Start + 1) * lineHeight;
        if (top + height < 0 || top > viewportHeight)
        {
            placement = default;
            return false;
        }
        placement = new DiffSideBlockPlacement(top, height);
        return true;
    }

    internal static Brush BrushFor(
        DiffSideBlock block, Brush modified, Brush added, Brush removed)
        => block is { HasAdd: true, HasRemove: true } ? modified
            : block.HasAdd ? added
            : removed;

    internal static DiffSideSelection CollectChangedLines(IReadOnlyList<DiffSideRowVm> rows, DiffSideBlock block)
    {
        var oldLines = new HashSet<int>();
        var newLines = new HashSet<int>();
        bool hasStaged = false, hasUnstaged = false;
        for (var index = block.Start; index <= block.End && index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.LeftKind == "Removed" && int.TryParse(row.LeftLine, out var oldLine))
            {
                oldLines.Add(oldLine);
                if (row.LeftStaged) hasStaged = true; else hasUnstaged = true;
            }
            if (row.RightKind == "Added" && int.TryParse(row.RightLine, out var newLine))
            {
                newLines.Add(newLine);
                if (row.RightStaged) hasStaged = true; else hasUnstaged = true;
            }
        }
        return new DiffSideSelection(oldLines, newLines, hasStaged, hasUnstaged);
    }

    /// <summary>
    /// 左右のエディタで選んだ行（行の添字 <paramref name="startRow"/>〜<paramref name="endRow"/>）が覆う変更行。
    /// 左右どちらで選んでも、同じ行の反対側の変更も含める（「b→B」の片側だけをステージすると、
    /// 消さずに足す／足さずに消すという、見た目と違う中途半端な状態がインデックスにできる）。
    /// <paramref name="wholeBlock"/>（選択が無くキャレットだけ）なら、その行を含む連続した変更丸ごと（<see cref="RegionOf"/>）。
    /// </summary>
    internal static DiffSideSelection CollectSelectedChanges(
        IReadOnlyList<DiffSideRowVm> rows, int startRow, int endRow, bool wholeBlock)
    {
        if (startRow > endRow) (startRow, endRow) = (endRow, startRow);
        if (startRow < 0 || rows.Count == 0)
            return DiffSideSelection.Empty;
        if (wholeBlock)
            return RegionOf(rows, startRow) is { } region ? CollectChangedLines(rows, region) : DiffSideSelection.Empty;
        return CollectChangedLines(rows, new DiffSideBlock(startRow, Math.Min(endRow, rows.Count - 1), false, false));
    }

    internal static IReadOnlyList<DiffSideBlock> Map(IReadOnlyList<DiffSideRowVm> rows)
    {
        var blocks = new List<DiffSideBlock>();
        var index = 0;
        while (index < rows.Count)
        {
            if (!IsChangeRow(rows[index]))
            {
                index++;
                continue;
            }

            var start = index;
            var stage = StageOf(rows[index]);
            var hasAdd = false;
            var hasRemove = false;
            while (index < rows.Count && IsChangeRow(rows[index]) && StageOf(rows[index]) == stage)
            {
                if (rows[index].RightKind == "Added") hasAdd = true;
                if (rows[index].LeftKind == "Removed") hasRemove = true;
                index++;
            }
            blocks.Add(new DiffSideBlock(start, index - 1, hasAdd, hasRemove, stage));
        }

        return blocks;
    }

    /// <summary>
    /// 行 <paramref name="rowIndex"/> を含む、連続した変更行のかたまり全体（ステージの状態では分けない）。
    /// 帯はステージ済み／未ステージで分けて描くが、「この変更をまとめて」はかたまり全体に効かせる。
    /// <see cref="DiffSideBlock.Stage"/> は全行ステージ済みなら All、1行も無ければ None、それ以外は Partial。
    /// </summary>
    internal static DiffSideBlock? RegionOf(IReadOnlyList<DiffSideRowVm> rows, int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= rows.Count || !IsChangeRow(rows[rowIndex])) return null;
        var start = rowIndex;
        while (start > 0 && IsChangeRow(rows[start - 1])) start--;
        var end = rowIndex;
        while (end + 1 < rows.Count && IsChangeRow(rows[end + 1])) end++;
        var stages = Enumerable.Range(start, end - start + 1).Select(i => StageOf(rows[i])).Distinct().ToList();
        var stage = stages.Count == 1 ? stages[0] : DiffStageState.Partial;
        var hasAdd = Enumerable.Range(start, end - start + 1).Any(i => rows[i].RightKind == "Added");
        var hasRemove = Enumerable.Range(start, end - start + 1).Any(i => rows[i].LeftKind == "Removed");
        return new DiffSideBlock(start, end, hasAdd, hasRemove, stage);
    }

    /// <summary>1行の変更がどこまでステージ済みか（左の削除・右の追加のうち、ある方だけを数える）。</summary>
    internal static DiffStageState StageOf(DiffSideRowVm row)
    {
        int changes = 0, staged = 0;
        if (row.LeftKind == "Removed") { changes++; if (row.LeftStaged) staged++; }
        if (row.RightKind == "Added") { changes++; if (row.RightStaged) staged++; }
        return staged == 0 ? DiffStageState.None : staged == changes ? DiffStageState.All : DiffStageState.Partial;
    }

    private static bool IsChangeRow(DiffSideRowVm row)
        => row.LeftKind is "Added" or "Removed" or "Empty"
           || row.RightKind is "Added" or "Removed" or "Empty";
}
