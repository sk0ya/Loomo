namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ実行ビューの一覧ペイン開閉と幅をWPFレイアウトへ反映する。</summary>
internal sealed class DebugPaneLayoutController(
    FrameworkElement content,
    UIElement splitter,
    ColumnDefinition splitterColumn,
    ColumnDefinition paneColumn,
    FrameworkElement rail,
    ContentControl toggle,
    string expandToolTip,
    string collapseToolTip)
{
    private const double CollapsedWidth = 28;
    private const double SplitterWidth = 6;
    private bool _isExpanded = true;
    private double _expandedWidth = 220;

    public void Toggle()
    {
        if (_isExpanded && paneColumn.ActualWidth > 0)
            _expandedWidth = paneColumn.ActualWidth;
        _isExpanded = !_isExpanded;

        content.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        splitter.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        splitterColumn.Width = new GridLength(_isExpanded ? SplitterWidth : 0);
        paneColumn.Width = new GridLength(_isExpanded ? _expandedWidth : CollapsedWidth);
        rail.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
        toggle.Content = _isExpanded ? "‹" : "›";
        toggle.ToolTip = _isExpanded ? collapseToolTip : expandToolTip;
    }
}
