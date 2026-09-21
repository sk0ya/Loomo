namespace sk0ya.Loomo.App.Services;

internal readonly record struct StageSurfaceState(
    bool Overview,
    PaneKind Pane,
    IReadOnlyCollection<PaneKind> ThumbnailKinds,
    IReadOnlyCollection<PaneKind> OverviewKinds,
    Size ThumbnailSize,
    double OverviewCardWidth,
    Action<PaneKind> SelectPane);

/// <summary>舞台と俯瞰レイヤーのWPF構成を更新・解除する。</summary>
internal sealed class StageSurfacePresenter(
    FrameworkElement paneHost,
    FrameworkElement stageHost,
    Panel stageArea,
    Panel wingStrip,
    Panel overviewPanel,
    FrameworkElement overviewLayer,
    StageThumbnailSourceController thumbnails,
    StageCardPresenter cards,
    Action<IReadOnlyCollection<PaneKind>> detachPaneElementsExcept,
    Action clearActivityBadges,
    Func<PaneKind, Border> buildLiveSlot)
{
    public Size VirtualSize(double effectiveWingWidth)
    {
        if (stageArea.ActualWidth > 0 && stageArea.ActualHeight > 0)
            return new Size(stageArea.ActualWidth, stageArea.ActualHeight);
        var hostW = stageHost.ActualWidth > 0 ? stageHost.ActualWidth : paneHost.ActualWidth;
        var hostH = stageHost.ActualHeight > 0 ? stageHost.ActualHeight : paneHost.ActualHeight;
        return new Size(
            Math.Max(hostW - effectiveWingWidth - 16, 480), // StageArea の左右マージン
            Math.Max(hostH - 18, 320));                     // 上下マージン
    }

    public void Rebuild(StageSurfaceState state)
    {
        thumbnails.Sync(state.ThumbnailKinds, state.ThumbnailSize);
        detachPaneElementsExcept(thumbnails.Kinds);
        stageArea.Children.Clear();
        overviewPanel.Children.Clear();
        clearActivityBadges();

        if (state.Overview)
        {
            overviewLayer.Visibility = Visibility.Visible;
            foreach (var kind in state.OverviewKinds)
                overviewPanel.Children.Add(cards.BuildSessionCard(
                    kind, state.OverviewCardWidth, isOverview: true, iconOnly: false,
                    onStage: kind == state.Pane, onClick: () => state.SelectPane(kind)));
        }
        else
        {
            overviewLayer.Visibility = Visibility.Collapsed;
            stageArea.Children.Add(buildLiveSlot(state.Pane));
        }
    }

    public void Clear()
    {
        detachPaneElementsExcept(Array.Empty<PaneKind>());
        stageArea.Children.Clear();
        wingStrip.Children.Clear();
        overviewPanel.Children.Clear();
        thumbnails.Clear();
        overviewLayer.Visibility = Visibility.Collapsed;
        stageHost.Visibility = Visibility.Collapsed;
        paneHost.Opacity = 1;
        paneHost.IsHitTestVisible = true;
    }
}
