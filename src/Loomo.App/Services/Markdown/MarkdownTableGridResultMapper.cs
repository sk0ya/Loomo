using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Core.Markdown;
using VGrid.Models;

namespace sk0ya.Loomo.App.Services;

/// <summary>編集済み VGrid 文書を Markdown テーブル用の行列へ写す。</summary>
internal static class MarkdownTableGridResultMapper
{
    public static MarkdownTableRegion CreateEmptyRegion()
        => new(
            0,
            0,
            new IReadOnlyList<string>[] { new[] { string.Empty } },
            Array.Empty<MarkdownColumnAlignment>());

    public static IReadOnlyList<IReadOnlyList<string>> ToRows(TsvDocument document)
        => document.Rows
            .Select(row => (IReadOnlyList<string>)row.Cells
                .Select(cell => cell.Value ?? string.Empty)
                .ToArray())
            .ToArray();
}
