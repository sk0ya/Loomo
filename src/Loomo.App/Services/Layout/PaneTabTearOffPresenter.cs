using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>メインペインのタブをドラッグして切り離す際のゴースト表示とOLEドラッグをまとめる。</summary>
internal static class PaneTabTearOffPresenter
{
    public static void Start(
        Window owner,
        UIElement source,
        TabEntryViewModel? entry,
        Func<DetachedItem> createItem,
        DetachedWindowManager detached)
    {
        if (Mouse.Captured is not null)
            Mouse.Capture(null);

        var container = entry is not null && source is ItemsControl host
            ? host.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement
            : null;
        if (container is not null)
            container.Opacity = TabDragGhost.TornSourceOpacity;

        using var ghost = TabDragGhost.Show(owner, entry?.Title ?? "タブ", entry?.Icon);
        void OnGiveFeedback(object _, GiveFeedbackEventArgs e) => ghost.Follow(e.Effects);
        source.GiveFeedback += OnGiveFeedback;
        detached.BeginExternalDrag(createItem, ghost);
        QueryContinueDragEventHandler onQueryContinue = (_, e) =>
        {
            if (e.EscapePressed)
                detached.CancelDrag();
        };
        source.QueryContinueDrag += onQueryContinue;

        try
        {
            var data = new DataObject(DetachedPaneWindow.DetachDragFormat, "external");
            var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
            detached.EndDrag(result);
        }
        finally
        {
            source.QueryContinueDrag -= onQueryContinue;
            source.GiveFeedback -= OnGiveFeedback;
            if (container is not null)
                container.Opacity = 1;
            detached.ClearDrag();
        }
    }
}
