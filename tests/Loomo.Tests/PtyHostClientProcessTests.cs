using System.Diagnostics;
using sk0ya.Loomo.Pty.Host;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 常駐ホストが「ほかの Loomo がまだ生きているか」を PID の再利用に騙されずに答えること（§34.6）。
/// 落ちた Loomo の PID を Windows Terminal の OpenConsole.exe が引き継ぎ、ホストがいつまでも終わらなかった（2026-11-06）。
/// </summary>
public sealed class PtyHostClientProcessTests
{
    private static Process StartIdle() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 60 127.0.0.1 >nul")
        {
            CreateNoWindow = true, UseShellExecute = false,
        })!;

    [Fact]
    public void AnOpenedClientIsAliveUntilItExits()
    {
        using var child = StartIdle();
        using var client = ClientProcess.TryOpen(child.Id);

        Assert.NotNull(client);
        Assert.False(client!.HasExited);

        child.Kill();
        child.WaitForExit();
        Assert.True(client.HasExited);
    }

    [Fact]
    public void ASameIdWithADifferentStartTimeIsAnotherProcess()
    {
        // ハンドルを開けなかったときの道：PID が別のプロセスに再利用されたら、起動時刻が合わないので「終わった」。
        using var child = StartIdle();
        try
        {
            using var reused = ClientProcess.FromIdentity(child.Id, child.StartTime.AddMinutes(-5));
            Assert.True(reused.HasExited);

            using var same = ClientProcess.FromIdentity(child.Id, child.StartTime);
            Assert.False(same.HasExited);

            using var unknown = ClientProcess.FromIdentity(child.Id, startTime: null);
            Assert.False(unknown.HasExited); // 分からなければ生きている側に倒す。
        }
        finally
        {
            child.Kill();
        }
    }
}
