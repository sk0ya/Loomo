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
    private readonly Func<BranchTreeNode, bool>? _prepareNode;
    private DispatcherTimer? _menuTimer;
    private TreeViewItem? _pendingItem;
    private int _downClickCount;
    private (long Tick, POINT Point)? _menuDismissClick;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

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
        ButtonBase? operationDelete = null,
        Func<BranchTreeNode, bool>? prepareNode = null)
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
        _prepareNode = prepareNode;

        _tree.PreviewMouseLeftButtonDown += OnTreeLeftButtonDown;
        _tree.PreviewMouseLeftButtonUp += OnTreeLeftButtonUp;
        _tree.ContextMenuOpening += OnContextMenuOpening;
        _tree.PreviewMouseRightButtonDown += OnTreeRightButtonDown;
        if (_tree.ContextMenu is { } menu)
        {
            menu.Closed += OnMenuClosed;
            if (_deferSingleClick)
                menu.AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent,
                    new MouseButtonEventHandler(OnMenuDownOutside), handledEventsToo: true);
        }
    }

    /// <summary>
    /// メニューを閉じたクリックに続けて押した2回目（WPF には単クリックとして届く）。引数は押下の
    /// <c>OriginalSource</c>。持ち主はダブルクリックと同じ処理をする
    /// （<see cref="GitBranchTreeClickPolicy.IsDoubleClickAfterMenuDismiss"/>）。
    /// </summary>
    internal event Action<object?>? DoubleClickAfterMenuDismiss;

    /// <summary>メニューの対象の行（ブランチ・タグ・サブモジュール・ワークツリー）。閉じたら外す。</summary>
    internal BranchTreeNode? TargetNode { get; private set; }

    internal GitBranchInfo? Target => TargetNode?.Branch;

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

    /// <summary>
    /// 押した時点のクリック回数を覚えておく。WPF の MouseUp の <c>ClickCount</c> はダブルクリックの
    /// 2回目でも 1 で届くため、離した側で数えると2回目が「単クリック」に化けてメニューを予約し直し、
    /// ダブルクリック（＝コミット一覧）の後にブランチ操作メニューまで出てしまう。
    /// </summary>
    private void OnTreeLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _downClickCount = e.ClickCount;
        if (_deferSingleClick && e.ClickCount == 1 && ConsumeMenuDismissClick()
            && FindRow(e.OriginalSource) is { DataContext: BranchTreeNode { IsFolder: false } })
        {
            // 1回目はメニューを閉じるのに使われて WPF に届いていない。これが2回目なので、
            // 離したときに単クリック扱い（メニューの予約し直し）にしない。
            _downClickCount = 2;
            CancelPendingMenu();
            DoubleClickAfterMenuDismiss?.Invoke(e.OriginalSource);
            return;
        }
        if (_deferSingleClick && e.ClickCount > 1)
            CancelPendingMenu();
    }

    private void OnMenuClosed(object? sender, RoutedEventArgs e) => TargetNode = null;

    /// <summary>
    /// メニューを開いている間（マウスをキャプチャしている間）に外を左で押した＝メニューを閉じるクリック。
    /// その時刻と位置を覚えておく（キーボード・項目の選択で閉じたときは来ない）。
    /// </summary>
    private void OnMenuDownOutside(object sender, MouseButtonEventArgs e)
    {
        _menuDismissClick = e.ChangedButton == MouseButton.Left && GetCursorPos(out var point)
            ? (Environment.TickCount64, point)
            : null;
    }

    private bool ConsumeMenuDismissClick()
    {
        if (_menuDismissClick is not { } dismiss)
            return false;
        _menuDismissClick = null;
        return GetCursorPos(out var point)
            && GitBranchTreeClickPolicy.IsDoubleClickAfterMenuDismiss(
                Environment.TickCount64 - dismiss.Tick,
                point.X - dismiss.Point.X, point.Y - dismiss.Point.Y,
                GetDoubleClickTime(), GetSystemMetrics(SmCxDoubleClk), GetSystemMetrics(SmCyDoubleClk));
    }

    private void OnTreeLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_deferSingleClick)
        {
            var item = WpfTreeTraversal.FindAncestor<TreeViewItem>(source);
            var clickedNode = item?.DataContext as BranchTreeNode;
            switch (GitBranchTreeClickPolicy.Resolve(
                Math.Max(e.ClickCount, _downClickCount),
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

        if (node.IsFolder)
        {
            TargetNode = null;
            row.IsExpanded = !row.IsExpanded;
            return;
        }

        row.IsSelected = true;
        OpenMenu(row);
        e.Handled = true;
    }

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        TargetNode = null;
        if (FindRow(e.OriginalSource) is not { } item)
            return;
        item.IsSelected = true;
        TargetNode = item.DataContext as BranchTreeNode;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindRow(e.OriginalSource);
        TargetNode = item?.DataContext as BranchTreeNode ?? _tree.SelectedItem as BranchTreeNode;
        if (!PrepareMenu(TargetNode))
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
        if (item is { IsLoaded: true, DataContext: BranchTreeNode { IsFolder: false } })
            OpenMenu(item);
    }

    private void OpenMenu(TreeViewItem item)
    {
        if (_tree.ContextMenu is not { } menu
            || item is not { IsLoaded: true, DataContext: BranchTreeNode { IsFolder: false } node })
            return;

        if (menu.IsOpen)
            menu.IsOpen = false;
        TargetNode = node;
        if (!PrepareMenu(node))
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

    /// <summary>
    /// メニューを対象の行に合わせる。フォルダ・見出しなら出さない。ブランチ以外の行（タグ・サブモジュール・
    /// ワークツリー）は持ち主（<c>prepareNode</c>）が項目の出し分けを持つ——無ければ出さない
    /// （ブランチ切替ポップアップのツリーにはブランチしか居ない）。
    /// </summary>
    private bool PrepareMenu(BranchTreeNode? node)
    {
        if (node is not { IsFolder: false })
            return false;
        if (_prepareNode is not null && !_prepareNode(node))
            return false;
        if (node.Branch is not { } branch)
            return _prepareNode is not null;
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
