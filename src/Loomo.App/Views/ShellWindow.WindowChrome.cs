
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
            // wParam の上位ワードが回転量（120/ノッチ、正＝右）。WPF に横スクロール対象が無いときは
            // EditorSupport の WebView（コンポジション版は WM_MOUSEHWHEEL を web へ転送しない）へ転送する。
            var delta = (short)(((long)wParam >> 16) & 0xFFFF);
            if (HorizontalWheelScroll.Handle(wParam) || TryHorizontalScrollEditorSupportWebView(delta))
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

}
