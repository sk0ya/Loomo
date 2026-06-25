using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: カスタムタイトルバー（WindowChrome）と最大化サイズ制御（マルチモニタ跨ぎ最大化）</summary>

/// <summary>ShellWindow: マルチモニタ跨ぎの疑似最大化（横並びモニタのワーク領域を連結した矩形へ広げ、
/// ペインをモニタ単位の列へ振り分けて継ぎ目跨ぎを防ぐ）。関連する Win32 P/Invoke と構造体もここに置く。
/// カスタムタイトルバーと WM_GETMINMAXINFO 処理は ShellWindow.WindowChrome.cs。</summary>
public partial class ShellWindow
{
    /// <summary>
    /// 現在のモニタのワーク領域 <paramref name="currentWork"/> と縦方向に重なる（＝横並びの）
    /// 全モニタのワーク領域を左から順に返す。縦積み等の重ならないモニタは含めない。
    /// </summary>
    private static List<RECT> GetSideBySideWorkAreas(RECT currentWork)
    {
        var works = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref info))
                    works.Add(info.rcWork);
                return true;
            },
            IntPtr.Zero);

        var result = works
            .Where(w => w.Bottom > currentWork.Top && w.Top < currentWork.Bottom)
            .OrderBy(w => w.Left)
            .ToList();
        return result.Count > 0 ? result : new List<RECT> { currentWork };
    }

    /// <summary>
    /// 疑似最大化（跨ぎ）先の矩形を求める。横並びの全モニタのワーク領域を横に連結し、
    /// 縦はそれらの共通帯に収める（どのモニタ上でもタスクバーを覆わない）。
    /// 横並びのモニタが無い（1枚・縦積み）場合は現在のワーク領域をそのまま返す。
    /// </summary>
    private static RECT ComputeMaximizeRect(RECT currentWork)
    {
        var result = currentWork;
        var top = currentWork.Top;
        var bottom = currentWork.Bottom;
        foreach (var w in GetSideBySideWorkAreas(currentWork))
        {
            result.Left = Math.Min(result.Left, w.Left);
            result.Right = Math.Max(result.Right, w.Right);
            top = Math.Max(top, w.Top);
            bottom = Math.Min(bottom, w.Bottom);
        }

        // 共通帯が成立しない（理論上のみ）場合は現在のワーク領域へフォールバック
        if (bottom <= top)
            return currentWork;
        result.Top = top;
        result.Bottom = bottom;
        return result;
    }

    /// <summary>疑似最大化（マルチモニタ跨ぎ）中か。WindowState は Normal のまま運用する。</summary>
    private bool _isSpanMaximized;
    /// <summary>疑似最大化に入る前のウィンドウ矩形（物理px）。復元に使う。</summary>
    private RECT? _spanRestoreBounds;
    /// <summary>疑似最大化に入る前のペインレイアウト（深いコピー）。解除時に復元する。
    /// 跨ぎ中の表示切替・ペイン移動はこのツリーへも反映し、スナップショット保存にもこれを使う。</summary>
    private PaneNode? _spanSavedRoot;

    /// <summary>
    /// 横並びのモニタがあれば、それらのワーク領域を連結した矩形へ疑似最大化する。
    /// 跨ぐ先が現在のモニタ1枚と変わらなければ何もせず false（通常の最大化に任せる）。
    /// </summary>
    private bool TrySpanMaximize()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        var work = monitorInfo.rcWork;
        var areas = GetSideBySideWorkAreas(work);
        var span = ComputeMaximizeRect(work);
        if (span.Left == work.Left && span.Right == work.Right)
            return false; // 横並びのモニタが無い

        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
        if (!GetWindowRect(hwnd, out var current))
            return false;
        _spanRestoreBounds = current;
        _isSpanMaximized = true;
        // リサイズ前の見た目（ペインの相対位置）を基準に振り分けてから広げる。
        ApplySpanPaneLayout(areas, span);
        SetWindowPos(hwnd, IntPtr.Zero,
            span.Left, span.Top, span.Right - span.Left, span.Bottom - span.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateMaximizeGlyph();
        return true;
    }

    /// <summary>
    /// 跨ぎ最大化用のペインレイアウトを適用する。表示中の各ペインを（現在の中心位置の比率で）
    /// 担当モニタへ振り分け、列の境界がモニタの継ぎ目と一致するルート列レイアウトへ組み替える。
    /// これでどのペインも継ぎ目を跨いで表示されない。元のレイアウトは
    /// <see cref="_spanSavedRoot"/> に保存し、跨ぎ解除時に復元する。
    /// 可視ペインが1枚しか無い・ズーム中の場合は組み替えない（継ぎ目跨ぎは許容）。
    /// </summary>
    private void ApplySpanPaneLayout(List<RECT> areas, RECT span)
    {
        _spanSavedRoot = null;
        if (areas.Count < 2 || _zoomedPane is not null)
            return;
        var visible = AllLeaves().Where(l => !l.Hidden).ToList();
        if (visible.Count < 2)
            return;
        var hostWidth = PaneHost.ActualWidth;
        var hostHeight = PaneHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
            return;

        CaptureLayoutSizes();
        var hiddenLeaves = AllLeaves().Where(l => l.Hidden).ToList();
        // 復元用は深いコピーで保存する（以降の列組み替えによるリーフ Weight 書き換えの影響を受けない）。
        _spanSavedRoot = _root is null ? null : BuildFromSnapshot(ToSnapshot(_root), new HashSet<PaneKind>());

        // 各ペインの現在の中心位置（PaneHost 内の比率）。矩形が取れないものは中央扱い。
        var infos = visible.Select(leaf =>
        {
            if (TryGetPaneRect(leaf.Kind, out var r))
                return (Leaf: leaf,
                        Cx: (r.X + r.Width / 2) / hostWidth,
                        Cy: (r.Y + r.Height / 2) / hostHeight,
                        Height: Math.Max(r.Height, 1.0));
            return (Leaf: leaf, Cx: 0.5, Cy: 0.5, Height: 1.0);
        }).ToList();

        // 中心位置の横比率を、スパン全体に対する各モニタの横範囲へ対応付けて割り当てる。
        var spanWidth = (double)(span.Right - span.Left);
        var groups = areas.Select(_ => new List<(PaneLeaf Leaf, double Cx, double Cy, double Height)>()).ToList();
        foreach (var info in infos)
        {
            var index = areas.FindIndex(a => info.Cx < (a.Right - span.Left) / spanWidth);
            groups[index < 0 ? areas.Count - 1 : index].Add(info);
        }

        // 空のモニタが出たら、左から順の均等割りへフォールバック（どのモニタにも1枚は置く）。
        if (groups.Any(g => g.Count == 0) && infos.Count >= areas.Count)
        {
            var ordered = infos.OrderBy(i => i.Cx).ThenBy(i => i.Cy).ToList();
            groups = areas.Select(_ => new List<(PaneLeaf Leaf, double Cx, double Cy, double Height)>()).ToList();
            for (var i = 0; i < ordered.Count; i++)
                groups[i * areas.Count / ordered.Count].Add(ordered[i]);
        }

        // 非表示リーフもツリーへ残す（跨ぎ中の再表示が列の中へ収まり、表示トグル・自動表示が機能し続ける）。
        // 画面上の矩形を持たないため、左端の（中身のある）列の最下段へ Hidden のまま置く。
        // 再表示されたときの高さは列内の平均に合わせる。
        foreach (var hiddenLeaf in hiddenLeaves)
        {
            var group = groups.FirstOrDefault(g => g.Count > 0) ?? groups[0];
            group.Add((hiddenLeaf, 0.0, double.MaxValue, group.Count > 0 ? group.Average(i => i.Height) : 1.0));
        }

        // PaneHost はウィンドウ左端から ActivityBar＋サイドバー分ずれているため、その分を
        // 左端モニタの列幅から差し引いて、列の境界が継ぎ目に乗るように重みを決める（物理px）。
        var dpi = VisualTreeHelper.GetDpi(this);
        var hostLeft = PaneHost.TransformToVisual(this).Transform(new Point(0, 0)).X * dpi.DpiScaleX;
        var hostRightGap = ActualWidth * dpi.DpiScaleX - hostLeft - hostWidth * dpi.DpiScaleX;

        var root = new PaneSplit { Orientation = SplitKind.Columns };
        double pending = 0;
        for (var i = 0; i < areas.Count; i++)
        {
            double width = areas[i].Right - areas[i].Left;
            if (i == 0)
                width -= hostLeft;
            if (i == areas.Count - 1)
                width -= hostRightGap;
            width += pending;
            if (groups[i].Count == 0)
            {
                pending = width; // 置くものが無いモニタの幅は右隣の列が吸収する
                continue;
            }
            pending = 0;
            root.Children.Add(BuildSpanColumn(groups[i], Math.Max(width, 1)));
        }
        if (pending > 0 && root.Children.Count > 0)
            root.Children[^1].Weight += pending;

        if (root.Children.Count < 2)
        {
            _spanSavedRoot = null; // 列分割が成立しないなら現状のまま
            return;
        }

        _zoomedPane = null;
        _root = root;
        RebuildPaneLayout();
    }

    /// <summary>1モニタ分の列ノード：割り当てられたペインを現在の縦位置順に縦積みする。</summary>
    private static PaneNode BuildSpanColumn(List<(PaneLeaf Leaf, double Cx, double Cy, double Height)> group, double weight)
    {
        var ordered = group.OrderBy(g => g.Cy).ThenBy(g => g.Cx).ToList();
        if (ordered.Count == 1)
        {
            ordered[0].Leaf.Weight = weight;
            return ordered[0].Leaf;
        }
        var rows = new PaneSplit { Orientation = SplitKind.Rows, Weight = weight };
        foreach (var item in ordered)
        {
            item.Leaf.Weight = item.Height;
            rows.Children.Add(item.Leaf);
        }
        return rows;
    }

    /// <summary>
    /// 跨ぎ最大化中にペインレイアウトの出どころが変わった（ワークスペース切替等）とき、
    /// 新しいレイアウトを基準に現在のモニタ構成で列振り分けを適用し直す。
    /// <see cref="_spanSavedRoot"/> も適用し直す直前のレイアウトで取り直されるため、
    /// 解除時・スナップショット保存時に切替前のレイアウトを引きずらない。
    /// </summary>
    private void ReapplySpanPaneLayout()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        // 適用直後のレイアウトでも実測のペイン矩形で振り分けられるよう、先にレイアウトを確定させる。
        PaneHost.UpdateLayout();
        ApplySpanPaneLayout(GetSideBySideWorkAreas(info.rcWork), ComputeMaximizeRect(info.rcWork));
    }

    /// <summary>疑似最大化を解き、入る前の矩形とペインレイアウトへ戻す。</summary>
    private void RestoreFromSpan()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_spanRestoreBounds is { } rect && hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, IntPtr.Zero,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOZORDER | SWP_NOACTIVATE);
        ExitSpanState();
    }

    /// <summary>
    /// 跨ぎ最大化中にタイトルバーのドラッグ移動が始まる直前、ウィンドウを跨ぐ前のサイズへ
    /// 縮めてカーソル下へ配置する（本物の最大化をドラッグで解除したときと同じ挙動）。
    /// カーソルの横位置のウィンドウ内比率を保つので、掴んだ点がタイトルバー上に残ったまま
    /// 移動ループへ入れる。ここで縮めた矩形がそのまま移動後のウィンドウサイズになる。
    /// </summary>
    private void ShrinkToSpanRestoreSizeAtCursor(IntPtr hwnd)
    {
        if (_spanRestoreBounds is not { } restore)
            return;
        if (!GetWindowRect(hwnd, out var current) || !GetCursorPos(out var cursor))
            return;

        var width = restore.Right - restore.Left;
        var height = restore.Bottom - restore.Top;
        var spanWidth = Math.Max(current.Right - current.Left, 1);
        var ratio = Math.Clamp((cursor.X - current.Left) / (double)spanWidth, 0.0, 1.0);
        var left = cursor.X - (int)Math.Round(width * ratio);
        SetWindowPos(hwnd, IntPtr.Zero, left, current.Top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>跨ぎ状態だけを解く（ウィンドウ矩形は触らない）。ペインレイアウトは跨ぐ前
    /// （＝跨ぎ中の表示切替・移動を反映済みの保存ツリー）へ復元する。</summary>
    private void ExitSpanState()
    {
        _isSpanMaximized = false;
        _spanRestoreBounds = null;
        if (_spanSavedRoot is { } saved)
        {
            _spanSavedRoot = null;
            _zoomedPane = null;
            _root = saved;
            RebuildPaneLayout();
            SaveActiveWorkspaceSnapshot();
        }
        UpdateMaximizeGlyph();
    }

    /// <summary>最大化／復元／跨ぎボタンの見た目を実状態（本物の最大化＋疑似最大化）へ同期する。</summary>
    private void UpdateMaximizeGlyph()
    {
        var maximized = _isSpanMaximized || WindowState == WindowState.Maximized;
        MaximizeIcon.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        MaximizeButton.ToolTip = maximized ? "元に戻す" : "最大化";
        SpanMaximizeButton.ToolTip = _isSpanMaximized ? "元に戻す" : "全モニタへ最大化";
    }

    /// <summary>跨ぎ最大化ボタンは横並びのモニタが2枚以上あるときだけ見せる。</summary>
    private void UpdateSpanButtonVisibility()
    {
        var visible = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                visible = GetSideBySideWorkAreas(info.rcWork).Count >= 2;
        }
        SpanMaximizeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}

