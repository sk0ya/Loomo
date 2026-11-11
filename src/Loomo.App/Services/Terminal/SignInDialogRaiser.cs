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
/// <para>別のアプリを見ている間に出たものは、<b>Loomo が前面に来たとき</b>に引き上げる（戻ってきたら奥に隠れていた、を防ぐ）。
/// 引き上げた後に人が Loomo をクリックして後ろへ回したなら、それは人の選択なので追いかけない
/// （定期的に前へ出し直すと、ダイアログを後回しにして Loomo を触ることができなくなる）。ただし引き上げた直後
/// （<see cref="SignInDialogPolicy.Grace"/> 以内）のクリックは、出た瞬間にたまたま触っただけでダイアログを見ていないので、
/// もう一度前に出す（<see cref="SignInDialogPolicy.MaxRaises"/> 回まで。取り合いにはしない）。</para>
/// </summary>
internal static class SignInDialogRaiser
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const uint GaRoot = 2;

    private static WinEventProc? _callback;   // フックが生きている間は GC させない
    private static readonly List<IntPtr> Hooks = [];
    private static readonly Dictionary<IntPtr, (DateTime Last, int Count)> Raised = [];

    /// <summary>UI スレッドから一度だけ呼ぶ（フックの知らせはこのスレッドのメッセージループに届く）。</summary>
    public static void Start()
    {
        if (_callback is not null)
            return;
        _callback = OnWinEvent;
        foreach (var ev in new[] { EventSystemForeground, EventObjectShow, EventObjectUncloaked })
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
        try
        {
            if (eventType == EventSystemForeground)
            {
                // Loomo に戻ってきた：留守の間に出て奥に隠れているものを探す
                if (IsOurs(hwnd))
                    RaisePending();
                return;
            }

            // 知らせは窓が出るたびに来るので、クラス名で先に絞る（ほとんどがここで抜ける）
            if (ClassOf(hwnd) != SignInDialogPolicy.FrameClass || GetAncestor(hwnd, GaRoot) != hwnd)
                return;
            TryRaise(hwnd);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"サインインの窓の引き上げに失敗: {ex.Message}");
        }
    }

    private static void RaisePending()
    {
        var frames = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (ClassOf(hwnd) == SignInDialogPolicy.FrameClass && MayRaise(hwnd))
                frames.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var frame in frames)
            TryRaise(frame);
    }

    private static void TryRaise(IntPtr frame)
    {
        if (!MayRaise(frame) || !IsWindowVisible(frame) || IsCloaked(frame))
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

        foreach (var gone in Raised.Keys.Where(h => !IsWindow(h)).ToList())
            Raised.Remove(gone);
        var count = Raised.TryGetValue(frame, out var previous) ? previous.Count : 0;
        Raised[frame] = (DateTime.UtcNow, count + 1);
        SetForegroundWindow(frame);
    }

    private static bool MayRaise(IntPtr frame)
        => !Raised.TryGetValue(frame, out var r) || SignInDialogPolicy.MayRaiseAgain(r.Last, r.Count, DateTime.UtcNow);

    private static bool LoomoIsForeground() => IsOurs(GetForegroundWindow());

    private static bool IsOurs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>クロークされた枠（中断中の UWP・出る途中の枠）は見えていないので触らない。</summary>
    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const int DwmwaCloaked = 14;

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
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

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

    /// <summary>引き上げてからこの間に Loomo へ戻ったのは、出た瞬間にたまたま触っただけとみなす。</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>1 つの窓を引き上げる回数の上限（何度も Loomo を触るなら、それは人の意思）。</summary>
    public const int MaxRaises = 3;

    /// <summary>一度引き上げた窓を、Loomo が前面に戻ったときにもう一度引き上げてよいか。</summary>
    public static bool MayRaiseAgain(DateTime lastRaisedUtc, int raisedCount, DateTime nowUtc)
        => raisedCount < MaxRaises && nowUtc - lastRaisedUtc < Grace;

    /// <param name="frameChildren">外枠の直下の子窓（クラス名・プロセス名）。</param>
    /// <param name="loomoIsForeground">いま前面にあるのが Loomo の窓か。</param>
    public static bool ShouldRaise(IEnumerable<(string Class, string Process)> frameChildren, bool loomoIsForeground)
        => loomoIsForeground && frameChildren.Any(c =>
            c.Class == ContentClass && string.Equals(c.Process, BrokerProcess, StringComparison.OrdinalIgnoreCase));
}
