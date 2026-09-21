using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>ペインタブのドラッグ開始、帯内の並べ替えとマウス捕捉をまとめる。</summary>
internal sealed class PaneTabDragInteractionController
{
    private const double ReorderVerticalTolerance = 6.0;

    private readonly Window _owner;
    private readonly Action<Guid, Guid> _moveTab;
    private readonly Action<Guid, UIElement?> _tearOff;
    private readonly Action _saveSnapshot;
    private Point _dragStart;
    private Guid _draggedId;
    private bool _dragArmed;
    private ItemsControl? _reorderHost;
    private Guid _reorderId;

    public PaneTabDragInteractionController(
        Window owner,
        Action<Guid, Guid> moveTab,
        Action<Guid, UIElement?> tearOff,
        Action saveSnapshot)
    {
        _owner = owner;
        _moveTab = moveTab;
        _tearOff = tearOff;
        _saveSnapshot = saveSnapshot;
    }

    public void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (ResolveTabId(e.OriginalSource) is not { } id)
            return;
        _dragStart = e.GetPosition(_owner);
        _draggedId = id;
        _dragArmed = true;
    }

    public void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_reorderHost is not null)
        {
            HandleReorderMove(e);
            return;
        }
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position = e.GetPosition(_owner);
        var dx = Math.Abs(position.X - _dragStart.X);
        var dy = Math.Abs(position.Y - _dragStart.Y);
        if (!PaneTabDragPolicy.ShouldBeginDrag(
                dx, dy, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
            return;

        _dragArmed = false;
        // ほぼ水平のドラッグは並べ替え、縦方向に動いたドラッグは別ウィンドウへの引き出しにする。
        if (PaneTabDragPolicy.ShouldReorder(
                dx, dy, SystemParameters.MinimumHorizontalDragDistance, ReorderVerticalTolerance)
            && sender is ItemsControl host)
        {
            StartReorder(_draggedId, host);
            return;
        }
        _tearOff(_draggedId, sender as UIElement);
    }

    public static Guid? ResolveTabId(object? source)
    {
        for (var current = source as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Tag: Guid id })
                return id;
        return null;
    }

    private void StartReorder(Guid id, ItemsControl host)
    {
        _reorderId = id;
        _reorderHost = host;
        host.PreviewMouseLeftButtonUp += OnReorderMouseUp;
        Mouse.Capture(host);
    }

    private void HandleReorderMove(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndReorder();
            return;
        }
        if (_reorderHost is not { } host)
            return;

        var position = e.GetPosition(host);
        if (position.X < 0 || position.Y < 0 || position.X > host.ActualWidth || position.Y > host.ActualHeight)
            return;
        if (VisualTreeHelper.HitTest(host, position)?.VisualHit is not { } hit ||
            ResolveTabId(hit) is not { } targetId || targetId == _reorderId)
            return;
        _moveTab(_reorderId, targetId);
    }

    private void OnReorderMouseUp(object sender, MouseButtonEventArgs e) => EndReorder();

    private void EndReorder()
    {
        if (_reorderHost is { } host)
            host.PreviewMouseLeftButtonUp -= OnReorderMouseUp;
        if (Mouse.Captured is not null)
            Mouse.Capture(null);
        var reordered = _reorderHost is not null;
        _reorderHost = null;
        if (reordered)
            _saveSnapshot();
    }
}
