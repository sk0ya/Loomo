namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: Windows から渡された「開いてほしいもの」を受ける（既定のアプリとしての振る舞い）。
/// 起動時の引数も、起動中に他のプロセスから中継されたもの（<see cref="InstanceRelayServer"/>）も同じ入口を通る。</summary>
public partial class ShellWindow {
    /// <summary>中継の受け口を据える。要求の処理は初フレームの後から——それまでに届いたものは受け口が溜めておく。</summary>
    internal void AttachExternalOpen(InstanceRelayServer relay, ExternalOpenRequest? startupRequest) {
        Activated += (_, _) => relay.MarkActive();
        void OnFirstRender(object? sender, EventArgs e) {
            ContentRendered -= OnFirstRender;
            relay.MarkActive();
            // 起動引数のフォルダーは App がウィンドウ生成前に部屋として開いてある。ここで開くのはファイルと URL だけ。
            if (startupRequest is { } request && (request.Files.Count > 0 || request.Urls.Count > 0))
                _ = OpenExternalAsync(request with { WorkspaceFolder = null }, bringToFront: false);
            relay.SetHandler(r => Dispatcher.BeginInvoke(() => _ = OpenExternalAsync(r, bringToFront: true)));
        }
        ContentRendered += OnFirstRender;
    }

    /// <summary>ファイルはエディタ（PDF・画像は EditorSupport）、URL はブラウザペインの新しいタブで開く。
    /// フォルダーは中継側が「この部屋が開いている」と判断したものだけが届くので、前面へ出すだけでよい。</summary>
    private async Task OpenExternalAsync(ExternalOpenRequest request, bool bringToFront) {
        if (bringToFront)
            BringToFrontForExternalOpen();
        foreach (var file in request.Files) {
            try { await OpenFileInNewEditorTabAsync(file); }
            catch (Exception ex) { ToastService.Error($"開けませんでした: {Path.GetFileName(file)} — {ex.Message}"); }
        }
        foreach (var url in request.Urls) {
            try { await OpenBrowserLibraryUrlAsync(url, newTab: true); }
            catch (Exception ex) { ToastService.Error($"開けませんでした: {url} — {ex.Message}"); }
        }
    }

    /// <summary>最小化されていれば元の状態（最大化を含む）へ戻して前面へ。前面化の権利は送り手が
    /// <c>AllowSetForegroundWindow</c> で譲ってくれている。</summary>
    private void BringToFrontForExternalOpen() {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Minimized && hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SwRestore);
        Activate();
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
