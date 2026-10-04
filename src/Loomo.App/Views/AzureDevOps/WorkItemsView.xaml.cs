using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ActivityBar の Work Items ポップアップの中身。</summary>
public partial class WorkItemsView : UserControl
{
    /// <summary>1回目のクリックからメニューを出すまでの待ち（この間に2回目が来たらダブルクリック）。</summary>
    private DispatcherTimer? _menuTimer;
    private Button? _pendingRow;

    public WorkItemsView() => InitializeComponent();

    /// <summary>開いたらすぐ打ち込めるように絞り込み欄へ。</summary>
    public void FocusFilter()
    {
        FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    /// <summary>Esc：文字があれば消す。空なら一覧を閉じる。Enter：残っている最初の行を開く。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WorkItemsViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (vm.FilterText.Length > 0) vm.FilterText = "";
            else vm.IsOpen = false;
        }
        else if (e.Key == Key.Enter && vm.Items.FirstOrDefault(r => r.CanOpen) is { } first)
        {
            e.Handled = true;
            vm.OpenCommand.Execute(first);
        }
    }

    /// <summary>
    /// 行のクリック：1回ならメニュー（開く・コピー）、ダブルクリックならブラウザペインで開く。
    /// 1回目ですぐメニューを出すと2回目がメニューに当たってしまうので、ダブルクリック時間だけ待ってから出す
    /// （ブランチツリーの GitBranchTreeMenuController と同じ）。
    /// </summary>
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button row) return;
        if (_menuTimer is not null && ReferenceEquals(_pendingRow, row))
        {
            CancelPendingMenu();
            if (DataContext is WorkItemsViewModel vm && row.DataContext is WorkItemListRowViewModel item)
                vm.OpenCommand.Execute(item);
            return;
        }
        CancelPendingMenu();
        _pendingRow = row;
        _menuTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(WindowNative.GetDoubleClickTime(), 1u)),
        };
        _menuTimer.Tick += OnMenuTimerTick;
        _menuTimer.Start();
    }

    private void OnMenuTimerTick(object? sender, EventArgs e)
    {
        var row = _pendingRow;
        CancelPendingMenu();
        if (row is not { IsLoaded: true, ContextMenu: { } menu }) return;
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void CancelPendingMenu()
    {
        if (_menuTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnMenuTimerTick;
        }
        _menuTimer = null;
        _pendingRow = null;
    }
}
