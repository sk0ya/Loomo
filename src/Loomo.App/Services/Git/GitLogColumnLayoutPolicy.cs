namespace sk0ya.Loomo.App.Services;

/// <summary>Git コミット一覧の固定列と余白を含む列幅計算。</summary>
internal static class GitLogColumnLayoutPolicy
{
    private const double FillColumnMinWidth = 120;

    /// <summary>中身・見出しの広い方へ、最後の列以外は列間の隙間も加えた幅を返す。</summary>
    internal static double LogTailColumnWidth(
        double contentWidth, double headerWidth, bool isLastColumn, double columnGap)
        => Math.Max(contentWidth, headerWidth) + (isLastColumn ? 0 : columnGap);

    /// <summary>ビューポートと列全体の差分を先頭列へ適用し、最小幅を保つ。</summary>
    internal static double LogFillColumnWidth(double currentWidth, double viewportWidth, double extentWidth)
        => Math.Max(FillColumnMinWidth, currentWidth + (viewportWidth - extentWidth));
}
