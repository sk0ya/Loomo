using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Pty.Host;
using sk0ya.Loomo.Services.Terminal;
using Terminal.Sessions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 端末から出たダイアログを Loomo の手前に（§34.12）。本物のシェル・本物の名前付きパイプ・同梱の ConPTY で、
/// 本体が送った窓が、ホストの持つ擬似コンソールの隠しコンソールウィンドウ（<c>PseudoConsoleWindow</c>）の持ち主になることを見る。
/// </summary>
public sealed class PtyHostOwnerWindowTests : IDisposable
{
    private readonly string _pipeName = "loomo-pty-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-pty-owner-" + Guid.NewGuid().ToString("N"));
    private readonly PtyHostClient _client;
    private readonly Mutex _hostMutex;

    public PtyHostOwnerWindowTests()
    {
        Directory.CreateDirectory(_root);
        var server = new PtyHostServer(_pipeName, idleExit: null);
        new Thread(server.Run) { IsBackground = true, Name = "PtyHostServerUnderTest" }.Start();
        _hostMutex = new Mutex(initiallyOwned: false, PtyProtocol.HostMutexName(_pipeName));
        _client = new PtyHostClient(_pipeName, Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
    }

    public void Dispose()
    {
        _hostMutex.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 持ち主の窓のハンドルはそのまま往復する()
    {
        var handle = new IntPtr(0x0001_2345_6789);
        Assert.Equal(handle, PtyProtocol.DecodeOwnerWindow(PtyProtocol.EncodeOwnerWindow(handle)));
        Assert.Equal(IntPtr.Zero, PtyProtocol.DecodeOwnerWindow([1, 2, 3]));
    }

    [Fact]
    public void ホストのコンソールウィンドウの持ち主が送った窓になる()
    {
        // 同梱の ConPTY（conpty.dll と x64\OpenConsole.exe）が揃っていないと、持ち主を付ける口が無い。
        var baseDirectory = AppContext.BaseDirectory;
        Assert.True(File.Exists(Path.Combine(baseDirectory, "conpty.dll")) &&
                    File.Exists(Path.Combine(baseDirectory, "x64", "OpenConsole.exe")),
            "テストの出力に同梱の ConPTY が無い");

        var owner = CreateWindowExW(0, "STATIC", "loomo-owner-test", WsPopup, 0, 0, 10, 10,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, owner);
        var before = ConsoleWindowsOwnedBy(owner);
        try
        {
            var session = _client.OpenSession(Guid.NewGuid(), Guid.NewGuid(), new TerminalSessionRequest(
                "cmd.exe", "cmd.exe /d /k prompt $G$G", _root, 80, 24, new Dictionary<string, string?>(), ScrollbackLimit: 1000));
            var text = new StringBuilder();
            session.OutputReceived += (_, chunk) => { lock (text) text.Append(chunk); };
            session.Start();
            WaitUntil(() => { lock (text) return text.ToString().Contains(">>"); }, "プロンプト");

            session.SetOwnerWindow(owner);

            WaitUntil(() => ConsoleWindowsOwnedBy(owner) > before, "隠しコンソールウィンドウの持ち主の付け替え");
            session.Dispose();
        }
        finally
        {
            DestroyWindow(owner);
        }
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"{what} が起きない");
            Thread.Sleep(50);
        }
    }

    /// <summary>持ち主が <paramref name="owner"/> の擬似コンソールの隠しウィンドウの数。</summary>
    private static int ConsoleWindowsOwnedBy(IntPtr owner)
    {
        var count = 0;
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            className.Clear();
            if (GetClassNameW(hwnd, className, className.Capacity) > 0
                && className.ToString() == "PseudoConsoleWindow"
                && GetWindow(hwnd, GwOwner) == owner)
                count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private const uint WsPopup = 0x80000000;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
}
