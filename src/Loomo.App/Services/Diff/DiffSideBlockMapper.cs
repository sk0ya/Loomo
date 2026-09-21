using sk0ya.Loomo.App.ViewModels;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct DiffSideBlock(int Start, int End, bool HasAdd, bool HasRemove);
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

    internal static (IReadOnlySet<int> OldLines, IReadOnlySet<int> NewLines) CollectChangedLines(
        IReadOnlyList<DiffSideRowVm> rows, DiffSideBlock block)
    {
        var oldLines = new HashSet<int>();
        var newLines = new HashSet<int>();
        for (var index = block.Start; index <= block.End && index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.LeftKind == "Removed" && int.TryParse(row.LeftLine, out var oldLine))
                oldLines.Add(oldLine);
            if (row.RightKind == "Added" && int.TryParse(row.RightLine, out var newLine))
                newLines.Add(newLine);
        }
        return (oldLines, newLines);
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
            var hasAdd = false;
            var hasRemove = false;
            while (index < rows.Count && IsChangeRow(rows[index]))
            {
                if (rows[index].RightKind == "Added") hasAdd = true;
                if (rows[index].LeftKind == "Removed") hasRemove = true;
                index++;
            }
            blocks.Add(new DiffSideBlock(start, index - 1, hasAdd, hasRemove));
        }

        return blocks;
    }

    private static bool IsChangeRow(DiffSideRowVm row)
        => row.LeftKind is "Added" or "Removed" or "Empty"
           || row.RightKind is "Added" or "Removed" or "Empty";
}
