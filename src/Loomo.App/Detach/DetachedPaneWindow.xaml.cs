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
using sk0ya.Loomo.App.Detach;

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

    private Point _dragStart;
    private DetachedItem? _pressedItem;

    internal DetachedPaneWindow(DetachedWindowManager manager)
    {
        _manager = manager;
        InitializeComponent();
        TabStripItems.ItemsSource = _items;
        Closed += OnWindowClosed;
        StateChanged += (_, _) => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";

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
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
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

    private static bool IsWithinButton(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase)
                return true;
        return false;
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    internal int ItemCount => _items.Count;
    internal bool Contains(DetachedItem item) => _items.Contains(item);

    /// <summary>項目をこのウィンドウへ追加してアクティブ表示する（実コントロールを再ペアレントする）。</summary>
    internal void AddItem(DetachedItem item)
    {
        _items.Add(item);
        ViewportTree.Detach(item.Content);
        item.Content.Visibility = Visibility.Collapsed;
        if (!ContentHost.Children.Contains(item.Content))
            ContentHost.Children.Add(item.Content);
        SetActive(item);
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

    // ===== ウィンドウ間ドラッグ&ドロップ =====

    private void OnTabPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _pressedItem = ResolveItem(e.OriginalSource);
    }

    private void OnTabPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem is null)
            return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _pressedItem;
        _pressedItem = null;

        // 子要素（閉じるボタン等）がマウスをキャプチャしていると DoDragDrop が始まらないため解放する。
        if (Mouse.Captured is not null)
            Mouse.Capture(null);

        _manager.BeginDrag(item, this);
        try
        {
            var data = new DataObject(DetachDragFormat, item.Id.ToString());
            var result = DragDrop.DoDragDrop(TabStripItems, data, DragDropEffects.Move);
            _manager.EndDrag(result);
        }
        finally
        {
            _manager.ClearDrag();
        }
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _manager.IsDragging && e.Data.GetDataPresent(DetachDragFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (_manager.IsDragging && e.Data.GetDataPresent(DetachDragFormat))
        {
            e.Handled = true;
            _manager.DropOnto(this);
        }
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
