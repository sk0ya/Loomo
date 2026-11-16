using System.Runtime.InteropServices;

namespace sk0ya.Loomo.McpBridge;

/// <summary>
/// 橋を起こした claude が<b>どの Loomo のどのタブのシェルの中か</b>を、環境変数ではなくプロセスの親子で見る（§45.3 P0b）。
/// 環境変数（<c>LOOMO_TERMINAL_TAB</c>）はシェルが生まれるときにしか渡らないので、機能より前から常駐しているシェル（§34）や
/// 設定を入れる前に開いたタブには無い。親をたどれば、シェルの PID と、その上の Loomo（自前の ConPTY）か常駐ホストに着く。
/// <para>プロセスの一覧は Toolhelp のスナップショット 1 回で取る（数 ms）。親の PID は使い回されることがあるので、
/// <b>子より後に生まれた親</b>に着いたらそこで止める。</para>
/// </summary>
internal static class ProcessAncestry
{
    public const string LoomoExe = "sk0ya.Loomo.App.exe";
    public const string PtyHostExe = "sk0ya.Loomo.Pty.Host.exe";

    /// <summary>このプロセスの祖先（近い順・自分は含まない）と、いま動いている Loomo の PID。</summary>
    public static (IReadOnlyList<int> Ancestors, bool UnderLoomo, IReadOnlySet<int>? LiveLoomos) Snapshot()
    {
        var processes = Enumerate();
        var live = processes.Values.Where(p => string.Equals(p.Exe, LoomoExe, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid).ToHashSet();
        var ancestors = new List<int>();
        var underLoomo = false;
        var current = Environment.ProcessId;
        var childCreated = CreationTime(current);
        for (var depth = 0; depth < 32 && processes.TryGetValue(current, out var entry); depth++)
        {
            var parent = entry.ParentPid;
            if (parent <= 4 || parent == current || !processes.TryGetValue(parent, out var parentEntry))
                break;
            var parentCreated = CreationTime(parent);
            if (parentCreated is { } p && childCreated is { } c && p > c)
                break; // 親の PID が使い回されている（本物の親はもう居ない）
            ancestors.Add(parent);
            if (IsLoomoProcess(parentEntry.Exe))
                underLoomo = true;
            current = parent;
            childCreated = parentCreated;
        }
        return (ancestors, underLoomo, live);
    }

    /// <summary>いま動いている Loomo の PID。</summary>
    public static IReadOnlySet<int> LiveLoomos()
        => Enumerate().Values.Where(p => string.Equals(p.Exe, LoomoExe, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid).ToHashSet();

    public static bool IsLoomoProcess(string exe)
        => string.Equals(exe, LoomoExe, StringComparison.OrdinalIgnoreCase)
           || string.Equals(exe, PtyHostExe, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Entry(int Pid, int ParentPid, string Exe);

    private static Dictionary<int, Entry> Enumerate()
    {
        var result = new Dictionary<int, Entry>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle)
            return result;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                var pid = (int)entry.th32ProcessID;
                result[pid] = new Entry(pid, (int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static long? CreationTime(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
