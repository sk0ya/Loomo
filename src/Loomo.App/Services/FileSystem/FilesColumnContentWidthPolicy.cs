using System;
using System.Collections.Generic;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>列見出しと表示中のセルから、ファイル一覧の列に必要な幅を算出する。</summary>
internal static class FilesColumnContentWidthPolicy
{
    private const double NameCellExtra = 40;
    private const double SizeCellExtra = 16;
    private const double CellExtra = 14;
    private const double HeaderCellExtra = 14;

    public static double Measure(
        FilesColumnKey key,
        string? label,
        IEnumerable<FileEntryViewModel> entries,
        double headerSize,
        double nameSize,
        FilesColumnTextMeasure text,
        FilesColumnTextMeasure badge)
    {
        var width = text.Width(label + " ▲", headerSize) + HeaderCellExtra;
        foreach (var entry in entries)
        {
            var cell = key switch
            {
                FilesColumnKey.Name => text.Width(entry.Name, nameSize) + NameCellExtra
                    + badge.Width(entry.GitStatusBadge, headerSize),
                FilesColumnKey.Size => text.Width(entry.SizeText, headerSize) + SizeCellExtra,
                FilesColumnKey.Modified => text.Width(entry.ModifiedText, headerSize) + CellExtra,
                FilesColumnKey.Type => text.Width(entry.TypeText, headerSize) + CellExtra,
                _ => 0,
            };
            if (cell > width)
                width = cell;
        }

        // FormattedText の実測と TextBlock の折り返し判定は端数で食い違うことがあるので、余白側へ丸める。
        return Math.Ceiling(width) + 1;
    }
}
