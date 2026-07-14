using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Detach;

/// <summary>
/// 切り離しウィンドウ（<see cref="DetachedPaneWindow"/>）群の管理と、タブドラッグの調停。
/// 2種類のドラッグを扱う：①既存の切り離しタブをウィンドウ間で移動、②メインペインのタブを引き出して
/// 別ウィンドウ化（外部ドラッグ。実体化は<b>ドロップ時</b>まで遅延し、途中キャンセルで元タブを消さない）。
/// <see cref="ShellWindow"/> が所有し、終了時に <see cref="CloseAll"/> で全ウィンドウ・全項目を破棄する。
/// </summary>
internal sealed class DetachedWindowManager
{
    private readonly Window _owner;
    private readonly List<DetachedPaneWindow> _windows = new();
    private readonly Action _changed;
    private bool _suppressChanged;

    // ===== ドラッグの一時状態（同時ドラッグは1つ） =====
    private DetachedItem? _dragItem;            // 既存項目（detached 窓由来）
    private DetachedPaneWindow? _dragSource;    // 既存項目の元窓
    private Func<DetachedItem>? _dragFactory;   // 外部（メインペインのタブ）由来の遅延生成器
    private bool _dropConsumed;
    private bool _dragCancelled;

    public DetachedWindowManager(Window owner, Action? changed = null)
    {
        _owner = owner;
        _changed = changed ?? (() => { });
    }

    internal bool IsDragging => _dragItem is not null || _dragFactory is not null;

    /// <summary>項目を新しいフローティングウィンドウで開く（切り離しの入口）。</summary>
    public void Detach(DetachedItem item)
    {
        var win = NewWindow();
        win.AddItem(item);
        win.Show();
        win.Activate();
        NotifyChanged();
    }

    private DetachedPaneWindow NewWindow(double? left = null, double? top = null)
    {
        var win = new DetachedPaneWindow(this) { Owner = _owner };
        if (left is { } l && top is { } t)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = l;
            win.Top = t;
        }
        _windows.Add(win);
        return win;
    }

    /// <summary>ウィンドウが閉じられたら管理から外す（<see cref="DetachedPaneWindow"/> の Closed から呼ばれる）。</summary>
    internal void OnWindowClosed(DetachedPaneWindow window)
    {
        _windows.Remove(window);
        NotifyChanged();
    }

    internal void NotifyChanged()
    {
        if (!_suppressChanged) _changed();
    }

    public List<DetachedWindowSnapshot> Capture(Func<DetachedItem, DetachedItemSnapshot?> captureItem)
        => _windows.Where(w => w.IsLoaded).Select(w => w.Capture(captureItem)).ToList();

    public void Restore(IEnumerable<DetachedWindowSnapshot> snapshots, Func<DetachedItemSnapshot, DetachedItem?> createItem)
    {
        _suppressChanged = true;
        try
        {
            CloseAll();
            foreach (var snapshot in snapshots)
            {
                var items = snapshot.Items.Select(createItem).Where(i => i is not null).Cast<DetachedItem>().ToList();
                if (items.Count == 0) continue;
                var win = NewWindow(snapshot.Left, snapshot.Top);
                win.Width = Math.Max(win.MinWidth, snapshot.Width);
                win.Height = Math.Max(win.MinHeight, snapshot.Height);
                foreach (var item in items) win.AddItem(item);
                win.Show();
                win.RestoreActiveIndex(snapshot.ActiveItemIndex);
                if (snapshot.IsMaximized) win.WindowState = WindowState.Maximized;
            }
        }
        finally { _suppressChanged = false; }
    }

    // ===== ドラッグ調停 =====

    /// <summary>既存の切り離しタブをウィンドウ間で移動するドラッグの開始。</summary>
    internal void BeginDrag(DetachedItem item, DetachedPaneWindow source)
    {
        _dragItem = item;
        _dragSource = source;
        _dragFactory = null;
        _dropConsumed = false;
        _dragCancelled = false;
    }

    /// <summary>メインペインのタブを引き出す外部ドラッグの開始（実体化はドロップ時まで遅延）。</summary>
    internal void BeginExternalDrag(Func<DetachedItem> factory)
    {
        _dragItem = null;
        _dragSource = null;
        _dragFactory = factory;
        _dropConsumed = false;
        _dragCancelled = false;
    }

    /// <summary>Esc 等でドラッグがキャンセルされた（元タブを消さない／新窓も作らない）。</summary>
    internal void CancelDrag() => _dragCancelled = true;

    /// <summary>いずれかのウィンドウのタブストリップへドロップされた：その項目を移送／実体化する。</summary>
    internal void DropOnto(DetachedPaneWindow target)
    {
        if (_dragCancelled)
            return;

        // 外部ドラッグ：ここで初めて実体化（メインから移動）して target へ載せる。
        if (_dragFactory is { } factory)
        {
            _dropConsumed = true;
            target.AddItem(factory());
            target.Activate();
            return;
        }

        if (_dragItem is not { } item || _dragSource is null)
            return;
        _dropConsumed = true;
        if (ReferenceEquals(_dragSource, target))
            return; // 同一ウィンドウ内ドロップは何もしない（並べ替えは未対応）

        _dragSource.RemoveItem(item, dispose: false);
        target.AddItem(item);
        target.Activate();
    }

    /// <summary>DoDragDrop 完了後の後処理：ドロップ先が無ければ新窓へ分離する（外部はメイン窓内で離すと復帰）。</summary>
    internal void EndDrag(DragDropEffects result)
    {
        if (_dropConsumed || _dragCancelled)
            return;

        // 外部ドラッグ（メインペインのタブ引き出し）：detached 窓ストリップへ落とせば結合済み（consumed）。
        // それ以外の場所で離したら新窓へ引き出す（Esc は _dragCancelled で除外済み）。メイン窓が最大化
        // していると「外側」が無くなり切り離せないため、位置によるスナップバックはしない。
        if (_dragFactory is { } factory)
        {
            SpawnAtCursor(factory());
            return;
        }

        // 既存タブの窓間ドラッグ：どのストリップにも受け取られなかった（ウィンドウ外）なら新窓へ分離。
        if (result != DragDropEffects.None)
            return;
        if (_dragItem is not { } item || _dragSource is not { } src || src.ItemCount <= 1)
            return;
        src.RemoveItem(item, dispose: false);
        SpawnAtCursor(item);
    }

    internal void ClearDrag()
    {
        _dragItem = null;
        _dragSource = null;
        _dragFactory = null;
        _dropConsumed = false;
        _dragCancelled = false;
    }

    private void SpawnAtCursor(DetachedItem item)
    {
        var (left, top) = CursorPositionDiu();
        var win = NewWindow(left - 40, top - 10);
        win.AddItem(item);
        win.Show();
        win.Activate();
    }

    /// <summary>アプリ終了時：全フローティングウィンドウを閉じ、全項目を破棄する。</summary>
    public void CloseAll()
    {
        _suppressChanged = true;
        foreach (var win in _windows.ToList())
            win.CloseAndDisposeItems();
        _windows.Clear();
        _suppressChanged = false;
    }

    // ===== カーソル位置ユーティリティ =====

    /// <summary>スクリーン座標のカーソル位置を DIU（WPF 論理座標）へ変換して返す（新窓の配置用）。</summary>
    private (double Left, double Top) CursorPositionDiu()
    {
        if (!GetCursorPos(out var p))
            return (200, 200);
        var dpi = VisualTreeHelper.GetDpi(_owner);
        return (p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
