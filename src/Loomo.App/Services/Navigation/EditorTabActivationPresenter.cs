using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタタブ切替後のヘッダー表示と共有ステータスバーを同期する。</summary>
internal sealed class EditorTabActivationPresenter
{
    private readonly Dispatcher _dispatcher;
    private readonly ScrollViewer _tabScrollViewer;
    private readonly ItemsControl _tabItems;
    private readonly Func<EditorTab?> _activeTab;

    public EditorTabActivationPresenter(
        Dispatcher dispatcher,
        ScrollViewer tabScrollViewer,
        ItemsControl tabItems,
        Func<EditorTab?> activeTab)
    {
        _dispatcher = dispatcher;
        _tabScrollViewer = tabScrollViewer;
        _tabItems = tabItems;
        _activeTab = activeTab;
    }

    public void OnActivated(EditorTab tab)
    {
        QueueHeaderIntoView(tab.Id);
        SyncSharedStatusBar(tab);
    }

    private void QueueHeaderIntoView(Guid id)
        => _dispatcher.BeginInvoke(
            new Action(() => ScrollHeaderIntoView(id)), DispatcherPriority.Loaded);

    private void ScrollHeaderIntoView(Guid id)
    {
        if (_tabScrollViewer.ViewportWidth <= 0)
            return;
        _tabItems.UpdateLayout();
        if (FindTabHeader(id, _tabItems) is not { } header)
            return;

        var bounds = header.TransformToAncestor(_tabScrollViewer)
            .TransformBounds(new Rect(0, 0, header.ActualWidth, header.ActualHeight));
        if (bounds.Left < 0)
            _tabScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, _tabScrollViewer.HorizontalOffset + bounds.Left));
        else if (bounds.Right > _tabScrollViewer.ViewportWidth)
            _tabScrollViewer.ScrollToHorizontalOffset(
                _tabScrollViewer.HorizontalOffset + bounds.Right - _tabScrollViewer.ViewportWidth);
    }

    private static FrameworkElement? FindTabHeader(Guid id, DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { DataContext: TabEntryViewModel tab } element && tab.Id == id)
                return element;
            if (FindTabHeader(id, child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>EditorSharedStatusBarは全エディタで共有され、どのコントロールも内容を書き込める。
    /// タブ切替後、裏タブの再ペアレントやレイアウトが古い内容を上書きするため、Background優先度でもう一度同期する。</summary>
    private void SyncSharedStatusBar(EditorTab tab)
    {
        if (!tab.IsRealized)
            return;
        tab.Control.SyncStatusBar();
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ReferenceEquals(_activeTab(), tab) && tab.IsRealized)
                tab.Control.SyncStatusBar();
        }));
    }
}
