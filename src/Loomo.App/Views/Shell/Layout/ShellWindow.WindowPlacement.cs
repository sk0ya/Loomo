namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: メインウィンドウの置き場所（通常時の矩形と、通常／最大化／全モニタへの最大化）を閉じるときに書き、
/// 次の起動でそこへ開く。保存先は <c>%APPDATA%/Loomo/window.json</c>（<see cref="ShellWindowPlacementStore"/>）。
/// 部屋の中身はワークスペースのスナップショットが戻すが、窓そのものはこの機械のモニタ構成に付くのでアプリ全体で 1 つ。</summary>
public partial class ShellWindow {
    private readonly ShellWindowPlacementStore _placementStore = new();
    /// <summary>起動時に戻す形。全モニタへの最大化はペインが並んでから（<see cref="RestoreStartupSpanMaximize"/>）。</summary>
    private ShellWindowShape _startupShape = ShellWindowShape.Normal;
    private ScreenRect? _startupBounds;
    /// <summary>「元に戻す」で戻る通常時の矩形（物理ピクセル）。最大化・全モニタへの最大化・ペイン全画面の間は更新しない。</summary>
    private RECT? _normalBounds;
    /// <summary>最小化される前の状態。最小化したまま閉じても、最大化していたなら最大化で開く。</summary>
    private WindowState _lastShownWindowState = WindowState.Normal;

    /// <summary>コンストラクタ（Show の前）から。位置を戻すなら WPF の中央寄せを止める。</summary>
    private void InitializeWindowPlacement() {
        if (_placementStore.Load() is { } placement) {
            _startupShape = placement.Shape;
            // 外したモニタ・解像度を下げたモニタの上に残った位置では開かない（大きさも捨てて既定で中央へ）。
            if (ShellWindowPlacementPolicy.IsReachable(placement.Bounds, AllMonitorWorkAreas())) {
                _startupBounds = placement.Bounds;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        LocationChanged += (_, _) => RememberNormalBounds();
        SizeChanged += (_, _) => RememberNormalBounds();
    }

    /// <summary>OnSourceInitialized（HWND ができて、まだ表示する前）から。</summary>
    private void ApplyStartupPlacement(IntPtr hwnd) {
        if (_startupBounds is { } b && hwnd != IntPtr.Zero) {
            // DPI の違うモニタへ移すと 1 回目は WM_DPICHANGED の推奨矩形で大きさが拡縮されるので、同じモニタの上で
            // もう 1 回置き直して保存した物理ピクセルどおりにする。
            for (var i = 0; i < 2; i++)
                SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            _normalBounds = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        } else {
            RememberNormalBounds();   // 中央寄せの既定の矩形。すぐ最大化すると以後掴めないので、ここで掴んでおく
        }
        // 全モニタへの最大化もまず通常の最大化で見せておく（ペインが並んでから広げ直す）。
        if (_startupShape is ShellWindowShape.Maximized or ShellWindowShape.Span)
            WindowState = WindowState.Maximized;
    }

    /// <summary>OnLoaded でワークスペースのペインを並べ終えてから。全モニタへの最大化はペインの位置からモニタごとの列を
    /// 組むので、並ぶ前には掛けられない。横並びのモニタが無くなっていれば通常の最大化のまま。</summary>
    private void RestoreStartupSpanMaximize() {
        if (_startupShape != ShellWindowShape.Span)
            return;
        _startupShape = ShellWindowShape.Normal;
        if (_isSpanMaximized || _paneFullscreen || WindowState == WindowState.Minimized)
            return;
        PaneHost.UpdateLayout();
        TrySpanMaximize();
    }

    private void RememberNormalBounds() {
        if (WindowState != WindowState.Normal || _isSpanMaximized || _paneFullscreen)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
            _normalBounds = rect;
    }

    private void RememberShownWindowState() {
        if (WindowState != WindowState.Minimized)
            _lastShownWindowState = WindowState;
    }

    private void SaveWindowPlacement() {
        if (CaptureWindowPlacement() is { } placement)
            _placementStore.Save(placement);
    }

    private ShellWindowPlacement? CaptureWindowPlacement() {
        RECT? bounds = _isSpanMaximized && _spanRestoreBounds is { } spanRestore ? spanRestore : _normalBounds;
        if (bounds is not { } b)
            return null;   // 一度も通常の矩形を掴めていない（開いてすぐ閉じた等）なら前回の保存を残す
        ShellWindowShape shape;
        if (_isSpanMaximized) {
            shape = ShellWindowShape.Span;
        } else {
            var state = _paneFullscreen ? _fullscreenPreviousWindowState : WindowState;
            if (state == WindowState.Minimized)
                state = _lastShownWindowState;
            shape = state == WindowState.Maximized ? ShellWindowShape.Maximized : ShellWindowShape.Normal;
        }
        return new ShellWindowPlacement(new ScreenRect(b.Left, b.Top, b.Right, b.Bottom), shape);
    }

    private static List<ScreenRect> AllMonitorWorkAreas() {
        var works = new List<ScreenRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) => {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref info))
                    works.Add(ToScreenRect(info.rcWork));
                return true;
            }, IntPtr.Zero);
        return works;
    }
}
