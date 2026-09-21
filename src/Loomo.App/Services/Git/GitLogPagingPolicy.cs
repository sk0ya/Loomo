namespace sk0ya.Loomo.App.Services;

/// <summary>ログ一覧が次ページを読み込む位置までスクロールしたかを判定する。</summary>
internal static class GitLogPagingPolicy
{
    internal static bool ShouldLoadMore(
        double verticalChange,
        double viewportHeightChange,
        double extentHeight,
        double verticalOffset,
        double viewportHeight)
    {
        if (verticalChange <= 0 && viewportHeightChange <= 0) return false;
        if (extentHeight <= 0) return false;
        var remaining = extentHeight - (verticalOffset + viewportHeight);
        return remaining <= viewportHeight;
    }
}
