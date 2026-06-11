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
public partial class ShellWindow
{
    // ===== カスタムタイトルバー（WindowChrome） =====

    // 単一の四角（最大化）/ 二重の四角（元に戻す）をベクターで描く。
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>直近に ActivityBar をクリックした時刻（TickCount64）。ダブルクリック自前判定用。</summary>
    private long _lastActivityBarClickTick;

    /// <summary>
    /// ActivityBar の空き領域をドラッグするとウィンドウを移動する（タイトルバーと同じ操作感）。
    /// アイコンボタン上のクリックはボタン側で処理済みのためここへは伝播しない。
    /// ダブルクリックは最大化／元に戻すをトグルする。
    /// </summary>
    private void OnActivityBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // ネイティブのキャプション移動ループに入ると WPF の ClickCount による
        // ダブルクリック判定が成立しないため、クリック間隔から自前で判定する。
        var now = Environment.TickCount64;
        var isDoubleClick = now - _lastActivityBarClickTick <= GetDoubleClickTime();
        _lastActivityBarClickTick = isDoubleClick ? 0 : now;

        if (isDoubleClick)
        {
            OnMaximizeRestore(sender, e);
            return;
        }

        // 最大化中はドラッグで動かさない（WindowChrome のタイトルバー側と挙動を揃える）。
        if (WindowState != WindowState.Normal)
            return;

        // DragMove() は WPF 経由でカクつくため、タイトルバー(WindowChrome)と同じ
        // ネイティブのキャプション移動ループ（HTCAPTION）へ委ねて滑らかに動かす。
        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (_isSpanMaximized)
        {
            RestoreFromSpan();
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>跨ぎ最大化ボタン：全モニタへの疑似最大化／復元をトグルする。</summary>
    private void OnSpanMaximize(object sender, RoutedEventArgs e)
    {
        if (_isSpanMaximized)
            RestoreFromSpan();
        else if (!TrySpanMaximize())
            WindowState = WindowState.Maximized; // 横並びのモニタが無ければ通常の最大化
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ===== 最大化サイズの制御（WindowStyle=None 対策＋マルチモニタ跨ぎ） =====
    //
    // WindowStyle="None" のボーダレスウィンドウは、最大化するとモニタ全体（タスクバー含む）に
    // 広がってしまい、最下部の AI バーがタスクバーの裏に隠れる。WM_GETMINMAXINFO を処理して
    // 最大化サイズをワーク領域（タスクバーを除いた範囲）に収める。
    //
    // マルチモニタ跨ぎ：本物の最大化（WS_MAXIMIZE）はウィンドウマネージャが1モニタへクリップする
    // ため、WM_GETMINMAXINFO で大きなサイズを返しても跨げない。そこで「疑似最大化」を行う：
    // WindowState は Normal のまま、横並び全モニタのワーク領域を連結した矩形へ SetWindowPos で
    // 広げ、復元用に元の矩形を保存する。入口はタイトルバーの専用ボタン（横並びモニタがあるとき
    // だけ表示）。通常の最大化ボタン・ダブルクリック・Win+↑ はデフォルトの1モニタ最大化のまま。
    // 跨ぎ中はペインをモニタ単位の列へ振り分け、どのペインも継ぎ目を跨がないようにする
    // （ApplySpanPaneLayout。解除時に元のレイアウトへ復元）。

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
        UpdateSpanButtonVisibility();
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int SC_SIZE = 0xF000;
    private const int SC_MOVE = 0xF010;
    private const int SC_MAXIMIZE = 0xF030;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HorizontalWheelScroll.WM_MOUSEHWHEEL)
        {
            if (HorizontalWheelScroll.Handle(wParam))
            {
                handled = true;
                // WM_MOUSEHWHEEL は処理したら TRUE を返す（WM_MOUSEWHEEL と逆の慣習）。
                return new IntPtr(1);
            }
        }
        else if (msg == WM_GETMINMAXINFO)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var work = monitorInfo.rcWork;
                    var mon = monitorInfo.rcMonitor;
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    // 最大化位置とサイズをワーク領域基準（モニタ左上からの相対）に設定する。
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        else if (msg == WM_SYSCOMMAND)
        {
            // 下位4ビットはマウス由来の付加情報なのでマスクして比較する。
            var command = (int)(wParam.ToInt64() & 0xFFF0);
            if (command == SC_MAXIMIZE && _isSpanMaximized)
            {
                // 跨ぎ中のタイトルバーダブルクリックは復元へ。
                // 通常時は既定の1モニタ最大化（デフォルトは跨がない）。
                RestoreFromSpan();
                handled = true;
            }
            else if (command is SC_MOVE or SC_SIZE && _isSpanMaximized)
            {
                // 跨ぎ中にユーザーが移動・リサイズを始めたら、その操作を尊重して跨ぎ状態だけ解く
                // （ペインレイアウトは跨ぐ前へ戻す）。タイトルバーのドラッグ移動（下位ビットが
                // HTCAPTION）は本物の最大化解除ドラッグと同じく、移動ループへ入る前に跨ぐ前の
                // サイズへ縮めてカーソル下に配置する。リサイズと keyboard 移動は矩形を触らない。
                if (command == SC_MOVE && (wParam.ToInt64() & 0x000F) == HTCAPTION)
                    ShrinkToSpanRestoreSizeAtCursor(hwnd);
                ExitSpanState();
            }
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            UpdateSpanButtonVisibility();
        }
        return IntPtr.Zero;
    }

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
