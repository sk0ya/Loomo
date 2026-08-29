using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 切り離した項目（<see cref="DetachedItem"/>）をタブとして表示するフローティングウィンドウ。
/// アクティブ項目の実コントロールだけを <c>ContentHost</c> に載せ、他は <see cref="Visibility.Collapsed"/> で
/// 退避する（ブラウザタブと同じ流儀）。タブはウィンドウ間をドラッグ&ドロップで移動でき、ウィンドウ外へ
/// 落とすと新しいウィンドウへ分離する（調停は <see cref="DetachedWindowManager"/>）。
/// </summary>
public partial class DetachedPaneWindow : Window
{
    /// <summary>ウィンドウ間タブドラッグのデータ形式（ペイロード本体は <see cref="DetachedWindowManager"/> が保持）。</summary>
    internal const string DetachDragFormat = "Loomo.DetachedTab";

    private readonly DetachedWindowManager _manager;
    private readonly ObservableCollection<DetachedItem> _items = new();

    internal DetachedPaneWindow(DetachedWindowManager manager)
    {
        _manager = manager;
        InitializeComponent();
        TabStripItems.ItemsSource = _items;
        TabOverflowList.ItemsSource = _items;
        Closed += OnWindowClosed;
        StateChanged += (_, _) => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        LocationChanged += (_, _) => {
            _manager.UpdateWindowDragTarget(this);
            _manager.NotifyChanged();
        };
        SizeChanged += (_, _) => _manager.NotifyChanged();
        StateChanged += (_, _) => _manager.NotifyChanged();

        // アイコンはコード側で assembly 修飾の pack URI から設定する（App 実行時のみ解決可。テスト等の
        // Application 無し環境では例外になるため握りつぶす）。
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/sk0ya.Loomo.App;component/Assets/Loomo.ico"));
        }
        catch { /* アイコン無しで続行 */ }
    }

    // ===== キャプションボタン（WindowChrome） =====

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>タイトルバーの空き領域ドラッグでウィンドウ移動（ダブルクリックで最大化トグル）。タブ・ボタン上は無視。</summary>
    private void OnCaptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        // タブ一覧のポップアップは別 HWND だが、押下は論理的な親である TitleBar まで浮いてくる。
        // 行ボタンの外側（枠や 4px の余白）を押しただけでウィンドウが動き出さないよう、帯そのものの
        // 上で押されたときだけ受ける。
        if (!IsWithinTitleBar(e.OriginalSource))
            return;
        if (ResolveItem(e.OriginalSource) is not null || IsWithinButton(e.OriginalSource))
            return;
        if (e.ClickCount == 2)
        {
            OnMaxRestore(sender, e);
            return;
        }
        if (WindowState == WindowState.Maximized)
            RestoreForCaptionDrag(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        _manager.BeginWindowDrag(this);
        try
        {
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        finally
        {
            // 標準のタイトルバー移動は WPF の Drop を通らないため、戻ってきた時点で
            // 別の切り離し窓上に着地していれば、管理側で全タブを結合する。
            _manager.EndWindowDrag(this);
        }
    }

    /// <summary>
    /// 最大化中のタイトルバーを掴んだ位置の下へ復元する。カスタムタイトルバーは非クライアント領域の
    /// 標準処理を通らないため、Windows が通常行う restore-on-drag をここで補う。
    /// </summary>
    private void RestoreForCaptionDrag(MouseButtonEventArgs e)
    {
        var cursor = PointToScreen(e.GetPosition(this));
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            cursor = target.TransformFromDevice.Transform(cursor);

        var restoredBounds = RestoreBounds;
        var captionPoint = e.GetPosition(this);
        var maximizedWidth = Math.Max(ActualWidth, 1);

        WindowState = WindowState.Normal;
        var position = CalculateRestoredTopLeft(cursor, captionPoint, maximizedWidth, restoredBounds);
        Left = position.X;
        Top = position.Y;
    }

    internal static Point CalculateRestoredTopLeft(
        Point cursor, Point captionPoint, double maximizedWidth, Rect restoredBounds)
    {
        var horizontalRatio = Math.Clamp(captionPoint.X / Math.Max(maximizedWidth, 1), 0, 1);
        return new Point(
            cursor.X - restoredBounds.Width * horizontalRatio,
            cursor.Y - captionPoint.Y);
    }

    /// <summary>押された要素が帯そのもの（＝この窓の視覚ツリー）の中か。ポップアップの中身は別の
    /// 視覚ツリー（PopupRoot）に居るので、ここで弾かれる。</summary>
    private bool IsWithinTitleBar(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, TitleBar))
                return true;
        return false;
    }

    private static bool IsWithinButton(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase)
                return true;
        return false;
    }

    /// <summary>WPF は WM_MOUSEHWHEEL を routed event にしないので、本体ウィンドウと同じくフックで拾う
    /// （切り離したブラウザ・エディタでもチルトホイールの横スクロールを効かせる）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOVE)
            _manager.UpdateWindowDragTarget(this);
        if (msg == HorizontalWheelScroll.WM_MOUSEHWHEEL && HorizontalWheelScroll.Handle(wParam))
        {
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;
    private const int WM_MOVE = 0x0003;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    internal int ItemCount => _items.Count;
    internal bool Contains(DetachedItem item) => _items.Contains(item);

    /// <summary>別窓を重ねたときの結合先案内を表示する。</summary>
    internal void SetMergeTarget(bool enabled)
        => MergeTargetFrame.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ドラッグ元に「離すと結合」を表示する。</summary>
    internal void SetMergeSourceHint(bool enabled)
        => MergeSourceHint.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>このウィンドウが抱える項目（ホスト側からの一括操作用。順序は表示順）。</summary>
    internal IEnumerable<DetachedItem> Items => _items;

    /// <summary>項目をこのウィンドウへ追加してアクティブ表示する（実コントロールを再ペアレントする）。</summary>
    internal void AddItem(DetachedItem item)
    {
        _items.Add(item);
        ViewportTree.Detach(item.Content);
        item.Content.Visibility = Visibility.Collapsed;
        if (!ContentHost.Children.Contains(item.Content))
            ContentHost.Children.Add(item.Content);
        SetActive(item);
        _manager.NotifyChanged();
    }

    /// <summary>項目をこのウィンドウから外す。<paramref name="dispose"/> が真なら破棄も行う
    /// （ウィンドウ間移動では false＝再ペアレントのため）。空になったらウィンドウを閉じる。</summary>
    internal void RemoveItem(DetachedItem item, bool dispose)
    {
        if (!_items.Remove(item))
            return;

        var wasActive = item.IsActive;
        ContentHost.Children.Remove(item.Content);
        ViewportTree.Detach(item.Content);
        if (dispose)
            item.Dispose();

        if (_items.Count == 0)
        {
            Close();
            return;
        }
        if (wasActive)
            SetActive(_items[^1]);
    }

    /// <summary>指定項目をアクティブにする（他は退避）。</summary>
    internal void SetActive(DetachedItem item)
    {
        foreach (var it in _items)
        {
            var on = ReferenceEquals(it, item);
            it.IsActive = on;
            it.Content.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        Title = $"{item.Title} — Loomo";
        QueueActiveTabIntoView();
        _manager.NotifyChanged();
    }

    internal DetachedWindowSnapshot Capture(Func<DetachedItem, DetachedItemSnapshot?> captureItem)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new DetachedWindowSnapshot
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            ActiveItemIndex = Math.Max(0, _items.ToList().FindIndex(i => i.IsActive)),
            Items = _items.Select(captureItem).Where(i => i is not null).Cast<DetachedItemSnapshot>().ToList()
        };
    }

    internal void RestoreActiveIndex(int index)
    {
        if (_items.Count > 0) SetActive(_items[Math.Clamp(index, 0, _items.Count - 1)]);
    }

    /// <summary>ウィンドウを閉じ、残っている全項目を破棄する（アプリ終了時の一括破棄用）。</summary>
    internal void CloseAndDisposeItems()
    {
        foreach (var item in _items.ToList())
        {
            ContentHost.Children.Remove(item.Content);
            ViewportTree.Detach(item.Content);
            item.Dispose();
        }
        _items.Clear();
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // ユーザーが × で閉じたときも残項目を破棄する（CloseAndDisposeItems 経由なら既に空）。
        foreach (var item in _items.ToList())
        {
            ContentHost.Children.Remove(item.Content);
            ViewportTree.Detach(item.Content);
            item.Dispose();
        }
        _items.Clear();
        _manager.OnWindowClosed(this);
    }

    // ===== タブ操作 =====

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        // 閉じるボタン経由で既に外れた項目は無視する（全項目が退避＝空表示になるのを防ぐ）。
        if (ResolveItem(e.OriginalSource) is { } item && _items.Contains(item))
            SetActive(item);
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetachedItem item })
        {
            e.Handled = true;
            RemoveItem(item, dispose: true);
        }
    }

    /// <summary>「▾」：あふれて見えなくなったタブも含む全件を一覧表示し、クリックで直接アクティブ化する
    /// （本体のタブ帯の <c>OnTabOverflowClick</c> と同じ導線。一覧は <c>_items</c> をそのまま映す）。</summary>
    private void OnTabOverflowClick(object sender, RoutedEventArgs e)
    {
        TabOverflowPopup.PlacementTarget = TabOverflowButton;
        TabOverflowPopup.IsOpen = _items.Count > 0;
    }

    private void OnTabOverflowItemClick(object sender, RoutedEventArgs e)
    {
        TabOverflowPopup.IsOpen = false;
        if (sender is FrameworkElement { DataContext: DetachedItem item } && _items.Contains(item))
            SetActive(item);
    }

    // ===== タブ帯のスクロール =====

    /// <summary>帯からあふれたタブはホイールで送る（Editor ペインの帯と同じ流儀＝横スクロールバーは出さない）。</summary>
    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.ScrollableWidth <= 0)
            return;
        viewer.ScrollToHorizontalOffset(
            Math.Clamp(viewer.HorizontalOffset - e.Delta, 0, viewer.ScrollableWidth));
        e.Handled = true;
    }

    /// <summary>アクティブなタブを帯の見える範囲へ寄せる（あふれた先のタブを選んでも隠れたままにしない）。
    /// レイアウト後でないと位置が定まらないので <see cref="DispatcherPriority.Loaded"/> で後追いする。</summary>
    private void QueueActiveTabIntoView()
        => Dispatcher.BeginInvoke(new Action(ScrollActiveTabIntoView), DispatcherPriority.Loaded);

    private void ScrollActiveTabIntoView()
    {
        // 並べ替え中は寄せない。掴んだタブは追従の <c>TranslateTransform</c> の分だけ右にずれて見えるので、
        // その位置で寄せると帯がドラッグの下で動き、掴んだタブがカーソルから離れて見える。
        if (_reorderItem is not null || TabStripScrollViewer.ViewportWidth <= 0)
            return;
        TabStripItems.UpdateLayout();
        if (_items.FirstOrDefault(i => i.IsActive) is not { } active
            || ContainerFor(active) is not { IsVisible: true } container)
            return;

        var bounds = container.TransformToAncestor(TabStripScrollViewer)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        if (bounds.Left < 0)
            TabStripScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, TabStripScrollViewer.HorizontalOffset + bounds.Left));
        else if (bounds.Right > TabStripScrollViewer.ViewportWidth)
            TabStripScrollViewer.ScrollToHorizontalOffset(
                TabStripScrollViewer.HorizontalOffset + bounds.Right - TabStripScrollViewer.ViewportWidth);
    }

    /// <summary>イベントの発生元要素から、それが属するタブの <see cref="DetachedItem"/> を辿る。</summary>
    private static DetachedItem? ResolveItem(object originalSource)
    {
        for (var d = originalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: DetachedItem item })
                return item;
        return null;
    }
}
