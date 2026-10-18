using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// プロセスの親子（Toolhelp のスナップショット）。デバッグアダプタが起こしたデバッグ対象を探すのに使う（§28.15）——
/// netcoredbg・NetFxDebug は launch で対象を<b>自分の子</b>として起こすが、DAP の <c>process</c> イベントを出すとは限らない。
/// </summary>
public static class ProcessTree
{
    /// <summary>デバッグ対象として数えない子（コンソールの付き添い）。</summary>
    private static readonly HashSet<string> Companions = new(StringComparer.OrdinalIgnoreCase) { "conhost.exe", "OpenConsole.exe" };

    /// <summary><paramref name="parentId"/> の直接の子のうち、デバッグ対象になりうるもの（conhost を除く）。読めなければ空。</summary>
    public static IReadOnlyList<int> ChildrenOf(int parentId)
    {
        var children = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
            return children;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
                return children;
            do
            {
                if (entry.th32ParentProcessID == (uint)parentId && entry.th32ProcessID != (uint)parentId
                    && !Companions.Contains(entry.szExeFile ?? ""))
                    children.Add((int)entry.th32ProcessID);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return children;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
