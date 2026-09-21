using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>GitLogColumnResizeController の生成と、列幅計算 API の互換ラッパー。</summary>
public partial class GitSessionView
{
    private GitLogColumnResizeController? _logColumnResizeController;

    private void SetupLogColumnResize()
    {
        _logColumnResizeController = new GitLogColumnResizeController(this, LogList, LogColumnResizeOverlay);
        _logColumnResizeController.Setup();
    }

    /// <summary>既存テスト・呼び出し元向け。列間隔には GridView のセル間隔を使う。</summary>
    internal static double LogTailColumnWidth(double contentWidth, double headerWidth, bool isLastColumn)
        => GitLogColumnLayoutPolicy.LogTailColumnWidth(
            contentWidth, headerWidth, isLastColumn, ColumnGapGridViewRowPresenter.ColumnGap);

    /// <summary>既存テスト・呼び出し元向けの先頭列幅計算。</summary>
    internal static double LogFillColumnWidth(double currentWidth, double viewportWidth, double extentWidth)
        => GitLogColumnLayoutPolicy.LogFillColumnWidth(currentWidth, viewportWidth, extentWidth);
}
