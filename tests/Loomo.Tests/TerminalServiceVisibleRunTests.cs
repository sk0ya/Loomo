using sk0ya.Loomo.Services;
using Terminal.Tabs;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 可視ターミナルでの実行が「打ち込めずに戻った」ときだけ、非表示プロセスでやり直す判定。
/// ターミナルペインを一度も開いていない部屋でビルドすると、出力も無く「失敗 (-1)」になっていた。
/// </summary>
public sealed class TerminalServiceVisibleRunTests
{
    private static readonly TerminalCommandResult NoShell = new("dotnet build", "", -1, false);

    [Fact]
    public void ReturnedAtOnceWithNothing_MeansTheShellWasNeverReached()
        => Assert.True(TerminalService.NeverReachedShell(NoShell, TimeSpan.FromMilliseconds(5), CancellationToken.None));

    [Fact]
    public void ATimeoutOrInterruptIsNotRunAgain()
    {
        // 人の中断・タイムアウトは出力を伴うか、時間がかかってから戻る。
        var interrupted = new TerminalCommandResult("dotnet build", "ビルド中...\n^C", -1, false);
        Assert.False(TerminalService.NeverReachedShell(interrupted, TimeSpan.FromMilliseconds(5), CancellationToken.None));
        Assert.False(TerminalService.NeverReachedShell(NoShell, TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    [Fact]
    public void ACancelledRunIsNotRunAgain()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(TerminalService.NeverReachedShell(NoShell, TimeSpan.FromMilliseconds(5), cts.Token));
    }

    [Fact]
    public void ACompletedRunIsKept()
    {
        var done = new TerminalCommandResult("dotnet build", "", 0, true);
        Assert.False(TerminalService.NeverReachedShell(done, TimeSpan.FromMilliseconds(5), CancellationToken.None));
    }
}
