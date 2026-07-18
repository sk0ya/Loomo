
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: F11 による現在ペインのモニター全画面表示。</summary>
public partial class ShellWindow
{
    private bool _paneFullscreen;
    private PaneKind? _fullscreenPane;
    private PaneKind? _fullscreenPreviousZoomedPane;
    private WindowState _fullscreenPreviousWindowState;
    private ResizeMode _fullscreenPreviousResizeMode;
    private bool _fullscreenPreviousTopmost;
    private NativeRect _fullscreenPreviousWindowRect;
    private Rect _fullscreenPreviousRestoreBounds;
    private GridLength _fullscreenActivityBarWidth;
    private GridLength _fullscreenTitleBarHeight;
    private GridLength _fullscreenSidebarWidth;
    private double _fullscreenSidebarMinWidth;
    private GridLength _fullscreenSidebarSplitterWidth;
    private GridLength _fullscreenWingWidth;
    private Thickness _fullscreenStageMargin;

    private void TogglePaneFullscreen()
    {
        if (_paneFullscreen)
        {
            ExitPaneFullscreen();
            return;
        }

        var target = _stageActive
            ? _stagePane
            : _focusedRegion?.Pane ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
        if (target is not { } pane || !_paneElements.ContainsKey(pane))
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var windowRect))
            return;
        var paneElement = _paneElements[pane];
        var center = paneElement.PointToScreen(
            new Point(paneElement.ActualWidth / 2, paneElement.ActualHeight / 2));
        var monitor = MonitorFromPoint(
            new NativePoint { X = (int)Math.Round(center.X), Y = (int)Math.Round(center.Y) },
            MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return;

        _paneFullscreen = true;
        _fullscreenPane = pane;
        _fullscreenPreviousZoomedPane = _zoomedPane;
        _fullscreenPreviousWindowState = WindowState;
        _fullscreenPreviousResizeMode = ResizeMode;
        _fullscreenPreviousTopmost = Topmost;
        _fullscreenPreviousWindowRect = windowRect;
        _fullscreenPreviousRestoreBounds = RestoreBounds;

        _fullscreenActivityBarWidth = ActivityBarColumn.Width;
        _fullscreenTitleBarHeight = TitleBarRow.Height;
        _fullscreenSidebarWidth = SidebarColumn.Width;
        _fullscreenSidebarMinWidth = SidebarColumn.MinWidth;
        _fullscreenSidebarSplitterWidth = SidebarSplitterColumn.Width;
        _fullscreenWingWidth = WingColumn.Width;
        _fullscreenStageMargin = StageArea.Margin;

        ActivityBarColumn.Width = new GridLength(0);
        TitleBarRow.Height = new GridLength(0);
        SidebarColumn.MinWidth = 0;
        SidebarColumn.Width = new GridLength(0);
        SidebarSplitterColumn.Width = new GridLength(0);
        WingColumn.Width = new GridLength(0);
        StageArea.Margin = new Thickness(0);

        _overviewActive = false;
        _zoomedPane = pane;
        RebuildPaneLayout();

        WindowState = WindowState.Normal;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        Topmost = true;
        var screen = monitorInfo.rcMonitor;
        SetWindowPos(hwnd, IntPtr.Zero,
            screen.Left, screen.Top, screen.Right - screen.Left, screen.Bottom - screen.Top,
            SwpNoZOrder | SwpNoActivate);
        FocusPane(pane);
    }

    private void ExitPaneFullscreen()
    {
        var pane = _fullscreenPane;
        var hwnd = new WindowInteropHelper(this).Handle;

        ActivityBarColumn.Width = _fullscreenActivityBarWidth;
        TitleBarRow.Height = _fullscreenTitleBarHeight;
        SidebarColumn.MinWidth = _fullscreenSidebarMinWidth;
        SidebarColumn.Width = _fullscreenSidebarWidth;
        SidebarSplitterColumn.Width = _fullscreenSidebarSplitterWidth;
        WingColumn.Width = _fullscreenWingWidth;
        StageArea.Margin = _fullscreenStageMargin;

        _zoomedPane = _fullscreenPreviousZoomedPane;
        _paneFullscreen = false;
        _fullscreenPane = null;
        RebuildPaneLayout();

        Topmost = _fullscreenPreviousTopmost;
        ResizeMode = _fullscreenPreviousResizeMode;
        WindowState = WindowState.Normal;
        if (_fullscreenPreviousWindowState == WindowState.Maximized)
        {
            Left = _fullscreenPreviousRestoreBounds.Left;
            Top = _fullscreenPreviousRestoreBounds.Top;
            Width = _fullscreenPreviousRestoreBounds.Width;
            Height = _fullscreenPreviousRestoreBounds.Height;
            WindowState = WindowState.Maximized;
        }
        else if (hwnd != IntPtr.Zero)
        {
            var rect = _fullscreenPreviousWindowRect;
            SetWindowPos(hwnd, IntPtr.Zero,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SwpNoZOrder | SwpNoActivate);
        }

        UpdateMaximizeGlyph();
        if (pane is { } focus)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusPane(focus)));
    }

}
