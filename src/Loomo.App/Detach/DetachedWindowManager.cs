using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
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

    // タイトルバーを使ったウィンドウ単位のドラッグ。Windows の標準移動ループ中は
    // WPF の Drop が発生しないため、移動開始時の位置と終了時のカーソル位置で結合を判定する。
    private DetachedPaneWindow? _windowDragSource;
    private DetachedPaneWindow? _windowDragTarget;
    private Point _windowDragStart;

    public DetachedWindowManager(Window owner, Action? changed = null)
    {
        _owner = owner;
        _changed = changed ?? (() => { });
    }

    internal bool IsDragging => _dragItem is not null || _dragFactory is not null;

    /// <summary>全ウィンドウの切り離し項目（ホスト側から種類で絞って一括操作するため）。</summary>
    internal IEnumerable<DetachedItem> AllItems => _windows.SelectMany(w => w.Items);

    /// <summary>項目を新しいフローティングウィンドウで開く（切り離しの入口）。</summary>
    public void Detach(DetachedItem item)
    {
        var win = NewWindow();
        win.AddItem(item);
        win.Show();
        win.Activate();
        NotifyChanged();
    }

    /// <summary>
    /// <paramref name="sibling"/> が居る窓へ、新しい項目を<b>タブとして</b>足して前へ出す
    /// （見つからなければ false＝呼び出し側が新しい窓を開く）。同じ用途の物を窓ごと増やさず
    /// 1つの窓のタブに集めるための入口——タブは掴んで引き出せば別窓にできるので、
    /// 「まとめる」を既定にしても並べて見比べる自由は残る。
    /// </summary>
    internal bool TryAddNextTo(DetachedItem sibling, DetachedItem item)
    {
        var window = _windows.FirstOrDefault(w => w.Contains(sibling));
        if (window is null)
            return false;
        window.AddItem(item);                 // AddItem がそのままアクティブタブにする
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        NotifyChanged();
        return true;
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

    /// <summary>切り離しウィンドウそのもののタイトルバー移動を開始する。</summary>
    internal void BeginWindowDrag(DetachedPaneWindow source)
    {
        ClearWindowDragFeedback();
        _windowDragSource = source;
        _windowDragStart = new Point(source.Left, source.Top);
    }

    /// <summary>タイトルバー移動中のカーソル位置に応じて、結合可能な窓の案内を更新する。</summary>
    internal void UpdateWindowDragTarget(DetachedPaneWindow source)
    {
        if (!ReferenceEquals(_windowDragSource, source))
            return;

        if (!HasMoved(source) || !WindowNative.GetCursorPos(out var cursor))
        {
            ClearWindowDragFeedback();
            return;
        }

        var target = FindWindowAt(cursor, source);
        if (ReferenceEquals(target, _windowDragTarget))
            return;

        _windowDragTarget?.SetMergeTarget(false);
        _windowDragTarget = target;
        _windowDragTarget?.SetMergeTarget(true);
        source.SetMergeSourceHint(target is not null);
    }

    /// <summary>
    /// タイトルバー移動が終わったとき、カーソルが別の切り離しウィンドウ上にあれば結合する。
    /// 標準の <c>WM_NCLBUTTONDOWN/HTCAPTION</c> 移動はアプリ内の DragDrop にならないため、
    /// ドロップイベントではなくここで後処理する。
    /// </summary>
    internal void EndWindowDrag(DetachedPaneWindow source)
    {
        if (!ReferenceEquals(_windowDragSource, source))
            return;

        try
        {
            // クリックだけでは結合しない。タイトルバー上で別窓が背後に重なっている場合でも、
            // クリックしただけで窓が消えるのは避ける。
            if (!HasMoved(source) || !WindowNative.GetCursorPos(out var cursor))
                return;

            UpdateWindowDragTarget(source);
            var target = _windowDragTarget ?? FindWindowAt(cursor, source);
            if (target is not null)
                MergeWindows(source, target);
        }
        finally
        {
            ClearWindowDragFeedback();
            _windowDragSource = null;
        }
    }

    private void ClearWindowDragFeedback()
    {
        _windowDragTarget?.SetMergeTarget(false);
        _windowDragTarget = null;
        _windowDragSource?.SetMergeSourceHint(false);
    }

    private bool HasMoved(DetachedPaneWindow source)
        => Math.Abs(source.Left - _windowDragStart.X) > 2
           || Math.Abs(source.Top - _windowDragStart.Y) > 2;

    private DetachedPaneWindow? FindWindowAt(WindowNative.NativePoint point, DetachedPaneWindow source)
    {
        var candidates = new Dictionary<IntPtr, DetachedPaneWindow>();
        foreach (var window in _windows)
        {
            if (ReferenceEquals(window, source)
                || !window.IsVisible
                || window.WindowState == WindowState.Minimized)
                continue;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !WindowNative.GetWindowRect(hwnd, out var bounds))
                continue;

            if (point.X >= bounds.Left && point.X < bounds.Right
                && point.Y >= bounds.Top && point.Y < bounds.Bottom)
                candidates[hwnd] = window;
        }

        if (candidates.Count == 0)
            return null;

        // _windows は生成順であり、Activate 後の見た目の前後関係を表さない。
        // デスクトップのトップレベル HWND を実際の Z 順（先頭が最前面）で走査し、
        // 重なった候補のうち画面上で最前面にある窓を結合先にする。
        var seen = new HashSet<IntPtr>();
        for (var hwnd = WindowNative.GetTopWindow(IntPtr.Zero);
             hwnd != IntPtr.Zero && seen.Add(hwnd);
             hwnd = WindowNative.GetWindow(hwnd, WindowNative.GwHwndNext))
        {
            if (candidates.TryGetValue(hwnd, out var target))
                return target;
        }

        // 列挙に失敗した場合も、当たり判定自体は捨てず最初の候補へ戻す。
        return candidates.Values.First();
    }

    /// <summary>ウィンドウ単位のドラッグで、元窓の全タブを相手窓へ移す。</summary>
    private void MergeWindows(DetachedPaneWindow source, DetachedPaneWindow target)
    {
        if (ReferenceEquals(source, target))
            return;

        var items = source.Items.ToList();
        if (items.Count == 0)
            return;
        var active = items.FirstOrDefault(item => item.IsActive);

        _suppressChanged = true;
        try
        {
            // RemoveItem は最後の項目を外した時点で元窓を閉じる。コンテンツは破棄せず、
            // target.AddItem が同じ実体を新しい ContentHost へ再ペアレントする。
            foreach (var item in items)
            {
                source.RemoveItem(item, dispose: false);
                target.AddItem(item);
            }

            if (active is not null && target.Contains(active))
                target.SetActive(active);
            target.Activate();
        }
        finally
        {
            _suppressChanged = false;
        }

        NotifyChanged();
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
            return; // 同一ウィンドウ内ドロップは何もしない（帯の中の並べ替えは
                    // DetachedPaneWindow.TabDrag の自前追従が済ませている）

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
        ClearWindowDragFeedback();
        _windowDragSource = null;
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
        if (!WindowNative.GetCursorPos(out var p))
            return (200, 200);
        var dpi = VisualTreeHelper.GetDpi(_owner);
        return (p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

}
