using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>1 セッションで使うデバッグアダプタ。<paramref name="Executable"/> は PATH 上の名前（netcoredbg）か
/// 絶対パス（同梱の NetFx アダプタ）。<paramref name="LaunchType"/> は launch/attach 引数の <c>type</c>。
/// <paramref name="MissingMessage"/> は未導入時にコンソールへ出す文言。</summary>
public sealed record DebugAdapterLaunch(
    string Executable,
    string[] Args,
    string AdapterId,
    string LaunchType,
    bool IsAvailable,
    string MissingMessage);

/// <summary>
/// デバッグ対象に合うアダプタを選ぶ。.NET（Core）は netcoredbg、.NET Framework は Loomo 同梱の NetFx アダプタ
/// （対象のビット数に合わせて 64bit／32bit）。判定は <see cref="ManagedProgramInspector"/>。
/// </summary>
public static class DebugAdapterResolver
{
    /// <summary>netcoredbg（.NET（Core）用）。</summary>
    public static DebugAdapterLaunch Netcoredbg => new(
        DebugAdapterCatalog.Netcoredbg.Executable,
        DebugAdapterCatalog.Netcoredbg.Args,
        "netcoredbg",
        "coreclr",
        ExecutableResolver.IsOnPath(DebugAdapterCatalog.Netcoredbg.Executable),
        $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。" +
        $"インストールしてください（例: {DebugAdapterCatalog.Netcoredbg.InstallCommand}）。");

    /// <summary>.NET Framework 用の同梱アダプタ。</summary>
    public static DebugAdapterLaunch NetFx(bool is32Bit)
    {
        var executable = DebugAdapterCatalog.NetFxExecutable(is32Bit);
        return new DebugAdapterLaunch(
            executable,
            [],
            "loomo-netfx",
            "clr",
            File.Exists(executable),
            $".NET Framework 用のデバッグアダプタが見つかりません: {executable}（Loomo を再ビルドしてください）。");
    }

    /// <summary>起動対象（.dll/.exe）に合うアダプタ。</summary>
    public static DebugAdapterLaunch ForProgram(string programPath)
    {
        var runtime = ManagedProgramInspector.Inspect(programPath);
        return runtime.Kind == ManagedRuntimeKind.NetFramework ? NetFx(runtime.Is32Bit) : Netcoredbg;
    }

    /// <summary>実行中プロセスに合うアダプタ。起動したばかりでランタイムがまだ読み込まれていないプロセスのために、
    /// 判定できるまで <paramref name="wait"/> だけ待つ（それでも分からなければ netcoredbg＝従来の経路）。</summary>
    public static async Task<DebugAdapterLaunch> ForProcessAsync(int processId, TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (ManagedProgramInspector.InspectProcess(processId) is { } runtime)
                return runtime.Kind == ManagedRuntimeKind.NetFramework ? NetFx(runtime.Is32Bit) : Netcoredbg;
            if (DateTime.UtcNow >= deadline) return Netcoredbg;
            await Task.Delay(200, ct);
        }
    }

    /// <summary>実行中プロセスに合うアダプタ。判定できなければ netcoredbg（従来の経路）。</summary>
    public static DebugAdapterLaunch ForProcess(int processId)
        => ManagedProgramInspector.InspectProcess(processId) is { Kind: ManagedRuntimeKind.NetFramework } runtime
            ? NetFx(runtime.Is32Bit)
            : Netcoredbg;
}
