using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Windows のサインイン（WAM）のアカウント選択が Loomo の後ろに出たら、手前へ引き上げる（§34.12.1）。
/// <para>§34.12 は隠しコンソール窓に持ち主を付けて、シェルから出たダイアログを手前に出す。だが Claude Code の MCP サーバーは
/// 窓の無いコンソールで起動されるので（<c>GetConsoleWindow</c> が 0）、付ける先が無い。Azure DevOps MCP（msal-node ＋ WAM）は
/// 親の窓を渡さずにアカウント選択を出し、持ち主の無い窓は前面にもならず Loomo の奥に隠れて、MCP の呼び出しが固まって見える。
/// Windows Terminal でも同じことが起きる。</para>
/// <para>出ている窓は「explorer の <c>ApplicationFrameWindow</c> の中に <c>Microsoft.AAD.BrokerPlugin</c> の
/// <c>Windows.UI.Core.CoreWindow</c>」（実測）。それが現れたとき Loomo が前面にいれば、前面の持ち主の権利で
/// <c>SetForegroundWindow</c> する。Loomo が前面でなければ触らない（人が今いる場所を奪わない）。</para>
/// </summary>
internal static class SignInDialogRaiser
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const uint GaRoot = 2;

    private static WinEventProc? _callback;   // フックが生きている間は GC させない
    private static readonly List<IntPtr> Hooks = [];
    private static readonly HashSet<IntPtr> Raised = [];

    /// <summary>UI スレッドから一度だけ呼ぶ（フックの知らせはこのスレッドのメッセージループに届く）。</summary>
    public static void Start()
    {
        if (_callback is not null)
            return;
        _callback = OnWinEvent;
        foreach (var ev in new[] { EventObjectShow, EventObjectUncloaked })
        {
            var hook = SetWinEventHook(ev, ev, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
            if (hook != IntPtr.Zero)
                Hooks.Add(hook);
        }
    }

    public static void Stop()
    {
        foreach (var hook in Hooks)
            UnhookWinEvent(hook);
        Hooks.Clear();
        _callback = null;
    }

    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || idChild != 0 || hwnd == IntPtr.Zero)
            return;
        // 知らせは窓が出るたびに来るので、クラス名で先に絞る（ほとんどがここで抜ける）
        if (ClassOf(hwnd) != SignInDialogPolicy.FrameClass || GetAncestor(hwnd, GaRoot) != hwnd)
            return;
        try
        {
            TryRaise(hwnd);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"サインインの窓の引き上げに失敗: {ex.Message}");
        }
    }

    private static void TryRaise(IntPtr frame)
    {
        if (Raised.Contains(frame) || !IsWindowVisible(frame))
            return;
        var contents = new List<(string Class, string Process)>();
        EnumChildWindows(frame, (child, _) =>
        {
            if (GetParent(child) == frame)
                contents.Add((ClassOf(child), ProcessNameOf(child)));
            return true;
        }, IntPtr.Zero);

        if (!SignInDialogPolicy.ShouldRaise(contents, LoomoIsForeground()))
            return;

        Raised.RemoveWhere(h => !IsWindow(h));
        Raised.Add(frame);
        SetForegroundWindow(frame);
    }

    private static bool LoomoIsForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static string ProcessNameOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);
}

/// <summary>引き上げるかどうかの判定（窓の中身の一覧だけを見る純粋関数）。</summary>
internal static class SignInDialogPolicy
{
    public const string FrameClass = "ApplicationFrameWindow";
    public const string ContentClass = "Windows.UI.Core.CoreWindow";
    public const string BrokerProcess = "Microsoft.AAD.BrokerPlugin";

    /// <param name="frameChildren">外枠の直下の子窓（クラス名・プロセス名）。</param>
    /// <param name="loomoIsForeground">いま前面にあるのが Loomo の窓か。</param>
    public static bool ShouldRaise(IEnumerable<(string Class, string Process)> frameChildren, bool loomoIsForeground)
        => loomoIsForeground && frameChildren.Any(c =>
            c.Class == ContentClass && string.Equals(c.Process, BrokerProcess, StringComparison.OrdinalIgnoreCase));
}
