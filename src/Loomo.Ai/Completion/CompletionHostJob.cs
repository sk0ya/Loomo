using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace sk0ya.Loomo.Ai.Completion;

/// <summary>
/// 先読みワーカーを本体と一蓮托生にする Job Object。
///
/// <para>本体が<b>行儀よく終われなかった</b>とき（クラッシュ、強制終了、デバッガの切断）に
/// ワーカーだけが残ると、500MB のモデルを抱えたプロセスが誰にも気づかれずに居座る。
/// <see cref="IDisposable"/> の後始末はそこまで面倒を見てくれないので、OS に任せる。</para>
///
/// <para>Job Object を作れない環境（権限の無いサンドボックス等）では黙って何もしない——
/// 取りこぼしはあり得ても、先読みのために本体が落ちる方がずっと悪い。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CompletionHostJob
{
    private static readonly IntPtr Handle = Create();

    /// <summary>このプロセスと運命を共にする job へ割り当てる。失敗しても何も起きない。</summary>
    public static void Assign(Process process)
    {
        if (Handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Handle, process.Handle); } catch { /* best-effort */ }
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extended, buffer, false);
                if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            return job;
        }
        catch { return IntPtr.Zero; }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
