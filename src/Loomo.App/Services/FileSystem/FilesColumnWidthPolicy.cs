using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧の自動列幅を、実測値と利用可能幅から決める。</summary>
internal static class FilesColumnWidthPolicy
{
    public static Dictionary<FilesColumnKey, double> ComputeAutoWidths(
        IReadOnlyDictionary<FilesColumnKey, double> contentWidths,
        IReadOnlyList<FilesColumnSetting> columns,
        double availableWidth)
    {
        var widths = new Dictionary<FilesColumnKey, double>(contentWidths);
        if (availableWidth <= 0 || !columns.Any(column => column.Key == FilesColumnKey.Name && column.IsVisible))
            return widths;

        var otherVisibleWidths = columns
            .Where(column => column.IsVisible && column.Key != FilesColumnKey.Name)
            .Sum(column => widths[column.Key]);
        widths[FilesColumnKey.Name] = availableWidth - otherVisibleWidths;
        return widths;
    }
}
