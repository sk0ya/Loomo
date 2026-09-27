using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>デバッグ対象が載る .NET ランタイムの種類。</summary>
public enum ManagedRuntimeKind
{
    /// <summary>.NET（Core）——netcoredbg で扱う。</summary>
    CoreClr,

    /// <summary>.NET Framework（デスクトップ CLR）——Loomo 同梱の NetFx デバッグアダプタで扱う。</summary>
    NetFramework,
}

/// <summary>デバッグ対象の実行形態。<paramref name="Is32Bit"/> はプロセスが 32bit で動くか
/// （ICorDebug はデバッガと対象のビット数が一致していないと接続できない）。</summary>
public sealed record ProgramRuntime(ManagedRuntimeKind Kind, bool Is32Bit);

/// <summary>
/// 実行ファイル・実行中プロセスが .NET Framework か .NET（Core）かを見分ける。
///
/// <para>起動対象：<c>.dll</c> は dotnet ホストで動く .NET（Core）。<c>.exe</c> は隣に
/// <c>*.runtimeconfig.json</c> があれば .NET（Core）の apphost、無くて CLI ヘッダーを持つ管理コードの exe なら
/// .NET Framework。ビット数は PE ヘッダーと CLI フラグから決める——旧形式の exe は既定で
/// <c>Prefer32Bit=true</c>（AnyCPU でも 32bit で動く）なので、ここを取り違えるとアタッチできない。</para>
/// </summary>
public static class ManagedProgramInspector
{
    /// <summary>起動対象のファイルを調べる。読めない・管理コードでないものは .NET（Core）扱い（従来の経路）。</summary>
    public static ProgramRuntime Inspect(string programPath)
    {
        if (!programPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return new ProgramRuntime(ManagedRuntimeKind.CoreClr, false);
        try
        {
            var runtimeConfig = Path.ChangeExtension(programPath, ".runtimeconfig.json");
            if (File.Exists(runtimeConfig)) return new ProgramRuntime(ManagedRuntimeKind.CoreClr, false);

            using var stream = File.OpenRead(programPath);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.CorHeader is not { } cor) return new ProgramRuntime(ManagedRuntimeKind.CoreClr, false);
            return new ProgramRuntime(ManagedRuntimeKind.NetFramework,
                Is32BitImage(headers.CoffHeader.Machine, headers.PEHeader?.Magic, cor.Flags));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new ProgramRuntime(ManagedRuntimeKind.CoreClr, false);
        }
    }

    /// <summary>PE／CLI ヘッダーから、64bit OS 上で 32bit プロセスとして動くかを決める（corflags と同じ規則）。
    /// PE32+ は 64bit。PE32 は ILONLY で 32BITREQUIRED/32BITPREFERRED のどちらも無いとき（純粋な AnyCPU）だけ 64bit。</summary>
    internal static bool Is32BitImage(Machine machine, PEMagic? magic, CorFlags flags)
    {
        if (magic == PEMagic.PE32Plus || machine is Machine.Amd64 or Machine.Arm64 or Machine.IA64) return false;
        if (!Environment.Is64BitOperatingSystem) return true;
        var ilOnly = (flags & CorFlags.ILOnly) != 0;
        var requires32 = (flags & CorFlags.Requires32Bit) != 0;
        var prefers32 = (flags & CorFlags.Prefers32Bit) != 0;
        return !ilOnly || requires32 || prefers32;
    }

    /// <summary>実行中プロセスのランタイムを調べる（アタッチ用）。<c>clr.dll</c>（.NET Framework 4）／<c>mscorwks.dll</c>
    /// （2.0 系）が載っていれば .NET Framework、<c>coreclr.dll</c> なら .NET（Core）。判定できなければ null。
    /// 64bit の Loomo から 32bit プロセスのモジュールを見るには <c>LIST_MODULES_ALL</c> が要る
    /// （<see cref="Process.Modules"/> は WOW64 側を返さない）。</summary>
    public static ProgramRuntime? InspectProcess(int processId)
    {
        const uint queryInformation = 0x0400, vmRead = 0x0010;
        var handle = OpenProcess(queryInformation | vmRead, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var is32Bit = Environment.Is64BitOperatingSystem && IsWow64Process(handle, out var wow64) && wow64;
            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(handle, modules, modules.Length * IntPtr.Size, out var needed, 0x03))
                return null;
            var count = Math.Min(modules.Length, needed / IntPtr.Size);
            var name = new char[260];
            ManagedRuntimeKind? kind = null;
            for (var i = 0; i < count; i++)
            {
                var length = GetModuleBaseNameW(handle, modules[i], name, name.Length);
                if (length <= 0) continue;
                var module = new string(name, 0, length);
                if (module.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                    return new ProgramRuntime(ManagedRuntimeKind.CoreClr, is32Bit);
                if (module.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                    module.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase))
                    kind = ManagedRuntimeKind.NetFramework;
            }
            // clr.dll と coreclr.dll の両方が載るプロセス（ホスト混在）は coreclr を優先する（上で即 return）。
            return kind is { } k ? new ProgramRuntime(k, is32Bit) : null;
        }
        catch (Win32Exception) { return null; }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size,
        out int needed, uint filter);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetModuleBaseNameW(IntPtr process, IntPtr module, [Out] char[] name, int size);
}
