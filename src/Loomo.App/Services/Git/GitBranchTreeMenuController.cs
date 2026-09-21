using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブランチツリーの行操作、メニュー対象、操作可否をまとめて管理する。</summary>
internal sealed class GitBranchTreeMenuController
{
    private readonly TreeView _tree;
    private readonly Func<bool> _hasRemote;
    private readonly bool _deferSingleClick;
    private readonly MenuItem _checkout;
    private readonly MenuItem _merge;
    private readonly MenuItem? _mergeStrategy;
    private readonly MenuItem _rebase;
    private readonly MenuItem _delete;
    private readonly MenuItem _deleteRemote;
    private readonly MenuItem _setUpstream;
    private readonly MenuItem _unsetUpstream;
    private readonly MenuItem _pull;
    private readonly MenuItem _push;
    private readonly MenuItem _pushForce;
    private readonly ButtonBase? _operationCheckout;
    private readonly ButtonBase? _operationMerge;
    private readonly ButtonBase? _operationDelete;
    private DispatcherTimer? _menuTimer;
    private TreeViewItem? _pendingItem;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    internal GitBranchTreeMenuController(
        TreeView tree,
        Func<bool> hasRemote,
        MenuItem checkout,
        MenuItem merge,
        MenuItem rebase,
        MenuItem delete,
        MenuItem deleteRemote,
        MenuItem setUpstream,
        MenuItem unsetUpstream,
        MenuItem pull,
        MenuItem push,
        MenuItem pushForce,
        bool deferSingleClick = false,
        MenuItem? mergeStrategy = null,
        ButtonBase? operationCheckout = null,
        ButtonBase? operationMerge = null,
        ButtonBase? operationDelete = null)
    {
        _tree = tree;
        _hasRemote = hasRemote;
        _deferSingleClick = deferSingleClick;
        _checkout = checkout;
        _merge = merge;
        _mergeStrategy = mergeStrategy;
        _rebase = rebase;
        _delete = delete;
        _deleteRemote = deleteRemote;
        _setUpstream = setUpstream;
        _unsetUpstream = unsetUpstream;
        _pull = pull;
        _push = push;
        _pushForce = pushForce;
        _operationCheckout = operationCheckout;
        _operationMerge = operationMerge;
        _operationDelete = operationDelete;

        _tree.PreviewMouseLeftButtonUp += OnTreeLeftButtonUp;
        _tree.ContextMenuOpening += OnContextMenuOpening;
        _tree.PreviewMouseRightButtonDown += OnTreeRightButtonDown;
        if (_tree.ContextMenu is { } menu)
            menu.Closed += (_, _) => Target = null;
    }

    internal GitBranchInfo? Target { get; private set; }

    internal void UpdateSelectionActions(GitBranchInfo? branch)
    {
        var availability = branch is null ? null : GitBranchActionPolicy.ForBranch(branch, _hasRemote());
        if (_operationCheckout is not null)
            _operationCheckout.IsEnabled = availability?.CanCheckout == true;
        if (_operationMerge is not null)
            _operationMerge.IsEnabled = availability?.CanMerge == true;
        if (_operationDelete is not null)
            _operationDelete.IsEnabled = availability?.CanDelete == true;
    }

    internal void CancelPendingMenu()
    {
        if (_menuTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnMenuTimerTick;
        }
        _menuTimer = null;
        _pendingItem = null;
    }

    private void OnTreeLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_deferSingleClick)
        {
            var item = WpfTreeTraversal.FindAncestor<TreeViewItem>(source);
            var clickedNode = item?.DataContext as BranchTreeNode;
            switch (GitBranchTreeClickPolicy.Resolve(
                e.ClickCount,
                WpfTreeTraversal.FindAncestor<ToggleButton>(source) is not null,
                clickedNode is not null,
                clickedNode?.IsFolder == true))
            {
                case GitBranchTreeClickAction.CancelPendingMenu:
                    CancelPendingMenu();
                    break;
                case GitBranchTreeClickAction.ToggleFolder when item is not null:
                    item.IsExpanded = !item.IsExpanded;
                    break;
                case GitBranchTreeClickAction.SelectBranch when item is not null:
                    item.IsSelected = true;
                    ScheduleMenu(item);
                    e.Handled = true;
                    break;
            }
            return;
        }

        var rowOrToggle = WpfTreeTraversal.FindAncestor<DependencyObject>(
            source, candidate => candidate is ToggleButton or TreeViewItem);
        if (rowOrToggle is ToggleButton || rowOrToggle is not TreeViewItem row
            || row.DataContext is not BranchTreeNode node)
            return;

        if (node.Branch is null)
        {
            Target = null;
            row.IsExpanded = !row.IsExpanded;
            return;
        }

        row.IsSelected = true;
        OpenMenu(row);
        e.Handled = true;
    }

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Target = null;
        if (FindRow(e.OriginalSource) is not { } item)
            return;
        item.IsSelected = true;
        Target = (item.DataContext as BranchTreeNode)?.Branch;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindRow(e.OriginalSource);
        Target = item?.DataContext is BranchTreeNode { Branch: { } branch }
            ? branch
            : (_tree.SelectedItem as BranchTreeNode)?.Branch;
        if (!PrepareMenu(Target))
        {
            e.Handled = true;
            return;
        }

        if (item is not null && _tree.ContextMenu is { } menu)
            PlaceMenu(menu, item);
    }

    private static TreeViewItem? FindRow(object? source)
    {
        var rowOrToggle = WpfTreeTraversal.FindAncestor<DependencyObject>(
            source as DependencyObject, candidate => candidate is ToggleButton or TreeViewItem);
        return rowOrToggle is ToggleButton ? null : rowOrToggle as TreeViewItem;
    }

    private void ScheduleMenu(TreeViewItem item)
    {
        CancelPendingMenu();
        _pendingItem = item;
        _menuTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(GetDoubleClickTime(), 1u))
        };
        _menuTimer.Tick += OnMenuTimerTick;
        _menuTimer.Start();
    }

    private void OnMenuTimerTick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer timer)
        {
            timer.Stop();
            timer.Tick -= OnMenuTimerTick;
        }

        var item = _pendingItem;
        _pendingItem = null;
        _menuTimer = null;
        if (item is { IsLoaded: true, DataContext: BranchTreeNode { Branch: not null } })
            OpenMenu(item);
    }

    private void OpenMenu(TreeViewItem item)
    {
        if (_tree.ContextMenu is not { } menu
            || item is not { IsLoaded: true, DataContext: BranchTreeNode { Branch: { } branch } })
            return;

        if (menu.IsOpen)
            menu.IsOpen = false;
        Target = branch;
        if (!PrepareMenu(branch))
            return;

        PlaceMenu(menu, item);
        menu.IsOpen = true;
    }

    private static void PlaceMenu(ContextMenu menu, TreeViewItem item)
    {
        menu.PlacementTarget = item;
        menu.Placement = PlacementMode.Right;
        menu.HorizontalOffset = 4;
        menu.StaysOpen = false;
    }

    private bool PrepareMenu(GitBranchInfo? target)
    {
        if (target is not { } branch)
            return false;
        var availability = GitBranchActionPolicy.ForBranch(branch, _hasRemote());
        _checkout.IsEnabled = availability.CanCheckout;
        _merge.IsEnabled = availability.CanMerge;
        if (_mergeStrategy is not null)
            _mergeStrategy.IsEnabled = availability.CanMerge;
        _rebase.IsEnabled = availability.CanRebase;
        _delete.IsEnabled = availability.CanDelete;
        _deleteRemote.Visibility = availability.ShowRemoteDelete ? Visibility.Visible : Visibility.Collapsed;
        _setUpstream.IsEnabled = availability.CanSetUpstream;
        _unsetUpstream.IsEnabled = availability.CanUnsetUpstream;
        _pull.IsEnabled = availability.CanPull;
        _push.IsEnabled = availability.CanPush;
        _pushForce.IsEnabled = availability.CanPush;
        return true;
    }
}
