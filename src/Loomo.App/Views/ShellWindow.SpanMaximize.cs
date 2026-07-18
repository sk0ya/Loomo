
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: カスタムタイトルバー（WindowChrome）と最大化サイズ制御（マルチモニタ跨ぎ最大化） ShellWindow: マルチモニタ跨ぎの疑似最大化（横並びモニタのワーク領域を連結した矩形へ広げ、 ペインをモニタ単位の列へ振り分けて継ぎ目跨ぎを防ぐ）。関連する Win32 P/Invoke と構造体もここに置く。 カスタムタイトルバーと WM_GETMINMAXINFO 処理は ShellWindow.WindowChrome.cs。</summary>
public partial class ShellWindow {
    private static List<RECT> GetSideBySideWorkAreas(RECT currentWork) {
        var works = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) => {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref info))
                    works.Add(info.rcWork);
                return true;
            }, IntPtr.Zero);

        var current = ToScreenRect(currentWork);
        return SpanLayoutPlanner.SideBySide(current, works.Select(ToScreenRect))
            .Select(ToNativeRect)
            .ToList();
    }

    private static RECT ComputeMaximizeRect(RECT currentWork) {
        var current = ToScreenRect(currentWork);
        return ToNativeRect(SpanLayoutPlanner.MaximizeRect( current, GetSideBySideWorkAreas(currentWork).Select(ToScreenRect)));
    }

    private static ScreenRect ToScreenRect(RECT rect)
        => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static RECT ToNativeRect(ScreenRect rect)
        => new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };

    private bool _isSpanMaximized;
    private RECT? _spanRestoreBounds;
    private PaneNode? _spanSavedRoot;

    private bool TrySpanMaximize() {
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
        ApplySpanPaneLayout(areas, span);
        SetWindowPos(hwnd, IntPtr.Zero, span.Left, span.Top, span.Right - span.Left, span.Bottom - span.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateMaximizeGlyph();
        return true;
    }

    private void ApplySpanPaneLayout(List<RECT> areas, RECT span) {
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
        _spanSavedRoot = _root is null ? null : BuildFromSnapshot(ToSnapshot(_root), new HashSet<PaneKind>());

        var infos = visible.Select(leaf => {
            if (TryGetPaneRect(leaf.Kind, out var r))
                return (Leaf: leaf, Cx: (r.X + r.Width / 2) / hostWidth, Cy: (r.Y + r.Height / 2) / hostHeight, Height: Math.Max(r.Height, 1.0));
            return (Leaf: leaf, Cx: 0.5, Cy: 0.5, Height: 1.0);
        }).ToList();

        var spanWidth = (double)(span.Right - span.Left);
        var groups = areas.Select(_ => new List<(PaneLeaf Leaf, double Cx, double Cy, double Height)>()).ToList();
        foreach (var info in infos) {
            var index = areas.FindIndex(a => info.Cx < (a.Right - span.Left) / spanWidth);
            groups[index < 0 ? areas.Count - 1 : index].Add(info);
        }

        if (groups.Any(g => g.Count == 0) && infos.Count >= areas.Count) {
            var ordered = infos.OrderBy(i => i.Cx).ThenBy(i => i.Cy).ToList();
            groups = areas.Select(_ => new List<(PaneLeaf Leaf, double Cx, double Cy, double Height)>()).ToList();
            for (var i = 0; i < ordered.Count; i++)
                groups[i * areas.Count / ordered.Count].Add(ordered[i]);
        }

        foreach (var hiddenLeaf in hiddenLeaves) {
            var group = groups.FirstOrDefault(g => g.Count > 0) ?? groups[0];
            group.Add((hiddenLeaf, 0.0, double.MaxValue, group.Count > 0 ? group.Average(i => i.Height) : 1.0));
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var hostLeft = PaneHost.TransformToVisual(this).Transform(new Point(0, 0)).X * dpi.DpiScaleX;
        var hostRightGap = ActualWidth * dpi.DpiScaleX - hostLeft - hostWidth * dpi.DpiScaleX;

        var root = new PaneSplit { Orientation = SplitKind.Columns };
        double pending = 0;
        for (var i = 0; i < areas.Count; i++) {
            double width = areas[i].Right - areas[i].Left;
            if (i == 0)
                width -= hostLeft;
            if (i == areas.Count - 1)
                width -= hostRightGap;
            width += pending;
            if (groups[i].Count == 0) {
                pending = width; // 置くものが無いモニタの幅は右隣の列が吸収する
                continue;
            }
            pending = 0;
            root.Children.Add(BuildSpanColumn(groups[i], Math.Max(width, 1)));
        }
        if (pending > 0 && root.Children.Count > 0)
            root.Children[^1].Weight += pending;

        if (root.Children.Count < 2) {
            _spanSavedRoot = null; // 列分割が成立しないなら現状のまま
            return;
        }

        _zoomedPane = null;
        _root = root;
        RebuildPaneLayout();
    }

    private static PaneNode BuildSpanColumn(List<(PaneLeaf Leaf, double Cx, double Cy, double Height)> group, double weight) {
        var ordered = group.OrderBy(g => g.Cy).ThenBy(g => g.Cx).ToList();
        if (ordered.Count == 1) {
            ordered[0].Leaf.Weight = weight;
            return ordered[0].Leaf;
        }
        var rows = new PaneSplit { Orientation = SplitKind.Rows, Weight = weight };
        foreach (var item in ordered) {
            item.Leaf.Weight = item.Height;
            rows.Children.Add(item.Leaf);
        }
        return rows;
    }

    private void ReapplySpanPaneLayout() {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        PaneHost.UpdateLayout();
        ApplySpanPaneLayout(GetSideBySideWorkAreas(info.rcWork), ComputeMaximizeRect(info.rcWork));
    }

    private void RestoreFromSpan() {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_spanRestoreBounds is { } rect && hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        ExitSpanState();
    }

    private void ShrinkToSpanRestoreSizeAtCursor(IntPtr hwnd) {
        if (_spanRestoreBounds is not { } restore)
            return;
        if (!GetWindowRect(hwnd, out var current) || !GetCursorPos(out var cursor))
            return;

        var width = restore.Right - restore.Left;
        var height = restore.Bottom - restore.Top;
        var spanWidth = Math.Max(current.Right - current.Left, 1);
        var ratio = Math.Clamp((cursor.X - current.Left) / (double)spanWidth, 0.0, 1.0);
        var left = cursor.X - (int)Math.Round(width * ratio);
        SetWindowPos(hwnd, IntPtr.Zero, left, current.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void ExitSpanState() {
        _isSpanMaximized = false;
        _spanRestoreBounds = null;
        if (_spanSavedRoot is { } saved) {
            _spanSavedRoot = null;
            _zoomedPane = null;
            _root = saved;
            RebuildPaneLayout();
            SaveActiveWorkspaceSnapshot();
        }
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph() {
        var maximized = _isSpanMaximized || WindowState == WindowState.Maximized;
        MaximizeIcon.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        MaximizeButton.ToolTip = maximized ? "元に戻す" : "最大化";
        SpanMaximizeButton.ToolTip = _isSpanMaximized ? "元に戻す" : "全モニタへ最大化";
    }

    private void UpdateSpanButtonVisibility() {
        var visible = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                visible = GetSideBySideWorkAreas(info.rcWork).Count >= 2;
        }
        SpanMaximizeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

}
