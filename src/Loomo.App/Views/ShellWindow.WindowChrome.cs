
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: カスタムタイトルバー（WindowChrome）と最大化サイズ制御（マルチモニタ跨ぎ最大化）</summary>
public partial class ShellWindow {

    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private long _lastActivityBarClickTick;

    private void OnActivityBarMouseDown(object sender, MouseButtonEventArgs e) {
        e.Handled = true;

        var now = Environment.TickCount64;
        var isDoubleClick = now - _lastActivityBarClickTick <= GetDoubleClickTime();
        _lastActivityBarClickTick = isDoubleClick ? 0 : now;

        if (isDoubleClick) {
            OnMaximizeRestore(sender, e);
            return;
        }

        if (WindowState != WindowState.Normal)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) {
        if (_isSpanMaximized) {
            RestoreFromSpan();
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnSpanMaximize(object sender, RoutedEventArgs e) {
        if (_isSpanMaximized)
            RestoreFromSpan();
        else if (!TrySpanMaximize())
            WindowState = WindowState.Maximized; // 横並びのモニタが無ければ通常の最大化
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();


    protected override void OnSourceInitialized(EventArgs e) {
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == HorizontalWheelScroll.WM_MOUSEHWHEEL) {
            var delta = (short)(((long)wParam >> 16) & 0xFFFF);
            if (HorizontalWheelScroll.Handle(wParam) || TryHorizontalScrollEditorSupportWebView(delta)) {
                handled = true;
                return new IntPtr(1);
            }
        } else if (msg == WM_GETMINMAXINFO) {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero) {
                var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref monitorInfo)) {
                    var work = monitorInfo.rcWork;
                    var mon = monitorInfo.rcMonitor;
                    var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        } else if (msg == WM_SYSCOMMAND) {
            var command = (int)(wParam.ToInt64() & 0xFFF0);
            if (command == SC_MAXIMIZE && _isSpanMaximized) {
                RestoreFromSpan();
                handled = true;
            } else if (command is SC_MOVE or SC_SIZE && _isSpanMaximized) {
                if (command == SC_MOVE && (wParam.ToInt64() & 0x000F) == HTCAPTION)
                    ShrinkToSpanRestoreSizeAtCursor(hwnd);
                ExitSpanState();
            }
        } else if (msg == WM_DISPLAYCHANGE) {
            UpdateSpanButtonVisibility();
        }
        return IntPtr.Zero;
    }

}
