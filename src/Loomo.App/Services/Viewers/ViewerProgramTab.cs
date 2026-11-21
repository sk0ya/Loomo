namespace sk0ya.Loomo.App.Services;

/// <summary>
/// パスから開いた、UI のあるプログラム（<c>.exe</c>）のタブ（§44.2「パスから開く」）。起動して、出てきたトップレベルの窓を
/// <b>子にせず</b>タブの場所へ重ね続ける（位置・大きさを合わせる・タブが隠れたら隠す・Loomo が前に出たらすぐ下へ回る）。
/// <para>子にしない（<c>SetParent</c>）・持ち主にしない理由：別プロセスの窓と親子／持ち主の関係を結ぶと入力のキューがつながり、
/// 相手が固まると Loomo も固まる。重ねるだけなら、相手が固まっても Loomo は動く（位置合わせは <c>SWP_ASYNCWINDOWPOS</c>）。</para>
/// <para>枠（タイトルバー・リサイズ枠）は外して重ね、手放すときに戻す。Loomo が起動したものはタブを閉じると終わらせる
/// （<c>CloseMainWindow</c>＝保存の確認はプログラムに任せる）。復元したタブは勝手に起動しない。</para>
/// </summary>
internal sealed class ViewerProgramTab : ViewerTabBase
{
    private enum Phase { Idle, Starting, Running, NoWindow, Exited }

    private readonly DispatcherTimer _sync;
    private Phase _phase = Phase.Idle;
    private Process? _process;
    private IntPtr _window;
    private IntPtr _originalStyle;
    private bool _shown;
    private NativeRect _lastRect;
    private Window? _owner;
    private CancellationTokenSource? _starting;

    public ViewerProgramTab(string targetPath, bool start, Panel host, Dispatcher dispatcher)
        : base(targetPath, host, dispatcher)
    {
        _sync = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _sync.Tick += (_, _) => Sync();
        dispatcher.ShutdownStarted += OnShutdown;
        SetStatus(ViewerTabStatus.Stopped);
        if (start)
            _ = StartAsync();
    }

    public override string ToolTip => TargetPath + "\nプログラムの窓をこのタブに重ねて表示";

    public override UIElement? BuildCard()
    {
        var name = ViewerTargets.NameOf(TargetPath!);
        var panel = ViewerCards.Panel();
        switch (_phase)
        {
            case Phase.Running:
                return null;
            case Phase.Starting:
                panel.Children.Add(ViewerCards.Line($"「{name}」を起動しています…", "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Line("窓が出たら、このタブに重ねます。", "Fs12", "FgDim"));
                panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button("待つのをやめる", "SecondaryButton", () => _starting?.Cancel())));
                return panel;
            case Phase.NoWindow:
                panel.Children.Add(ViewerCards.Line($"「{name}」の窓が見つかりません", "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Line("起動したプロセスがすぐ終わったか、別のプロセスに窓を渡したのかもしれません（その窓はこのタブには重ねられません）。", "Fs12", "FgDim"));
                break;
            case Phase.Exited:
                panel.Children.Add(ViewerCards.Line($"「{name}」は終わりました", "Fs14", "Fg", bold: true));
                break;
            default:
                panel.Children.Add(ViewerCards.Line(name, "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Line("起動すると、その窓をこのタブに重ねます。勝手には起動しません。", "Fs12", "FgDim"));
                break;
        }
        panel.Children.Add(ViewerCards.Mono(TargetPath!, "Fs11", "FgDim", top: 8));
        panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button(_phase == Phase.Idle ? "起動する" : "もう一度起動する", "FlatButton",
            () => _ = StartAsync(), "ViewerProgramStartButton")));
        return panel;
    }

    public override Task RefreshAsync(bool reload = false)
    {
        OnShownChanged();
        return Task.CompletedTask;
    }

    /// <summary>起動して、窓が出るのを待って重ねる。</summary>
    private async Task StartAsync()
    {
        if (_phase is Phase.Starting or Phase.Running || Disposed)
            return;
        _phase = Phase.Starting;
        SetStatus(ViewerTabStatus.Pending);
        using var starting = _starting = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var path = TargetPath!;
            _process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            ToastService.Error("起動できませんでした：" + ex.Message);
            _phase = Phase.Idle;
            SetStatus(ViewerTabStatus.Stopped);
            return;
        }
        var process = _process;
        IntPtr window = IntPtr.Zero;
        try
        {
            while (!starting.IsCancellationRequested && process is not null && !Disposed)
            {
                window = await Task.Run(() => FindWindow(process));
                if (window != IntPtr.Zero || process.HasExited)
                    break;
                await Task.Delay(200, starting.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        _starting = null;
        if (Disposed)
            return;
        if (window == IntPtr.Zero)
        {
            _phase = Phase.NoWindow;
            SetStatus(ViewerTabStatus.Stopped);
            return;
        }
        Adopt(window);
    }

    /// <summary>そのプロセスのトップレベルの窓（見えていて、持ち主の無いもの）。</summary>
    private static IntPtr FindWindow(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
                return IntPtr.Zero;
            if (process.MainWindowHandle != IntPtr.Zero && IsWindowVisible(process.MainWindowHandle))
                return process.MainWindowHandle;
            var pid = (uint)process.Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                    return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return IntPtr.Zero;
        }
    }

    private void Adopt(IntPtr window)
    {
        _window = window;
        _originalStyle = GetWindowLongPtr(window, GwlStyle);
        var style = (long)_originalStyle & ~(WsCaption | WsThickFrame);
        SetWindowLongPtr(window, GwlStyle, new IntPtr(style));
        _shown = IsWindowVisible(window);
        _lastRect = default;
        if (_process is { } process)
        {
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Dispatcher.BeginInvoke(OnProgramExited);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
        _phase = Phase.Running;
        SetStatus(ViewerTabStatus.Running);
        _sync.Start();
        Sync(force: true);
    }

    private void OnProgramExited()
    {
        if (Disposed || _phase != Phase.Running)
            return;
        _sync.Stop();
        _window = IntPtr.Zero;
        _phase = Phase.Exited;
        SetStatus(ViewerTabStatus.Stopped);
    }

    protected override void OnShownChanged() => Sync(force: true);

    /// <summary>窓をタブの場所へ合わせる（見えないときは隠す）。</summary>
    private void Sync(bool force = false)
    {
        if (_window == IntPtr.Zero || Disposed)
            return;
        if (!IsWindow(_window))
        {
            OnProgramExited();
            return;
        }
        var owner = Window.GetWindow(Host);
        if (!ReferenceEquals(owner, _owner))
        {
            if (_owner is not null)
                _owner.Activated -= OnOwnerActivated;
            _owner = owner;
            if (_owner is not null)
                _owner.Activated += OnOwnerActivated;
        }
        var show = CanShow && owner is { WindowState: not WindowState.Minimized } && Host.ActualWidth > 4 && Host.ActualHeight > 4
                   && PresentationSource.FromVisual(Host) is not null;
        if (!show)
        {
            if (_shown)
            {
                ShowWindowAsync(_window, SwHide);
                _shown = false;
            }
            return;
        }
        var topLeft = Host.PointToScreen(new Point(0, 0));
        var bottomRight = Host.PointToScreen(new Point(Host.ActualWidth, Host.ActualHeight));
        var rect = new NativeRect
        {
            Left = (int)Math.Round(topLeft.X), Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(bottomRight.X), Bottom = (int)Math.Round(bottomRight.Y),
        };
        if (!force && _shown && rect.Equals(_lastRect))
            return;
        var flags = SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos | SwpFrameChanged | SwpShowWindow;
        SetWindowPos(_window, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, flags);
        var wasShown = _shown;
        _shown = true;
        _lastRect = rect;
        if (!wasShown || force)
            PutOwnerBelow();
    }

    /// <summary>Loomo が前に出た：Loomo を重ねた窓のすぐ下へ回す（重ねた窓を他のアプリより前へは出さない）。</summary>
    private void OnOwnerActivated(object? sender, EventArgs e)
    {
        if (_shown)
            PutOwnerBelow();
    }

    private void PutOwnerBelow()
    {
        if (_owner is null || _window == IntPtr.Zero)
            return;
        var ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (ownerHandle != IntPtr.Zero)
            SetWindowPos(ownerHandle, _window, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>重ねるのをやめ、枠を戻して素の窓に戻す。</summary>
    private void Release()
    {
        _sync.Stop();
        if (_owner is not null)
            _owner.Activated -= OnOwnerActivated;
        _owner = null;
        if (_window == IntPtr.Zero)
            return;
        if (IsWindow(_window))
        {
            SetWindowLongPtr(_window, GwlStyle, _originalStyle);
            SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpAsyncWindowPos);
            ShowWindowAsync(_window, SwShowNoActivate);
        }
        _window = IntPtr.Zero;
        _shown = false;
    }

    protected override void Unmount()
    {
        // 入れ物が替わる：窓はそのまま。次の Sync で新しい入れ物の場所へ合わせる。
        if (_owner is not null)
            _owner.Activated -= OnOwnerActivated;
        _owner = null;
    }

    private void OnShutdown(object? sender, EventArgs e) => Dispose();

    public override void Dispose()
    {
        if (Disposed)
            return;
        Dispatcher.ShutdownStarted -= OnShutdown;
        _starting?.Cancel();
        var window = _window;
        Release();
        // Loomo が起動したものは終わらせる（保存の確認はプログラムに任せる）。
        try
        {
            if (_process is { HasExited: false } process)
                process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        _process?.Dispose();
        _process = null;
        _ = window;
        base.Dispose();
    }

    // ── Win32 ───────────────────────────────────────────────

    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const uint GwOwner = 4;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpAsyncWindowPos = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect : IEquatable<NativeRect>
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Equals(NativeRect other)
            => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        public override readonly bool Equals(object? obj) => obj is NativeRect other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
