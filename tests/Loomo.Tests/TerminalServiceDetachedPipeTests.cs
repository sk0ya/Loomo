using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 切り離された孫プロセスが標準出力のハンドルを握ったままでも、AI の手番が返ることの回帰ガード。
/// <para>出力の読み取りを専用スレッドへ移した（§31.16）とき、<c>WaitForExitAsync</c> は
/// <b>プロセスの終了しか待たない</b>ようになった——<c>BeginOutputReadLine</c> の頃は読み切りまで
/// 中で待ち、しかもそれは打ち切りトークンで縛られていた。読み切りを素の <c>await</c> にすると、
/// 親シェルが終わっても孫がパイプを握っている場合に EOF が来ず、手番が永遠に返らなくなる
/// （<c>npm run dev</c>、残った node など、この部屋で実際に踏んできた形）。</para>
/// 実プロセスを起こす数秒級のテストだが、この性質は単体では再現できない。
/// </summary>
public sealed class TerminalServiceDetachedPipeTests
{
    [Fact]
    public async Task 孫がパイプを握ったままでも打ち切りで返る()
    {
        var service = new TerminalService();
        // 親 pwsh はすぐ終わるが、孫は**標準出力ハンドルを継承して**生き続けるのでパイプが閉じない。
        // （Start-Process はハンドルを継承しないので、継承する素の Process.Start で起こす。）
        const string command =
            "$i = New-Object System.Diagnostics.ProcessStartInfo 'pwsh'; " +
            "$i.Arguments = '-NoProfile -Command \"Start-Sleep 20\"'; " +
            "$i.UseShellExecute = $false; " +
            "[System.Diagnostics.Process]::Start($i) | Out-Null; " +
            "Write-Output 'parent-done'";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var clock = Stopwatch.StartNew();

        var result = await service.RunCommandAsync(command, cts.Token);

        clock.Stop();
        // 孫がパイプを握っている＝親の終了だけでは読み切りが終わらないので、打ち切り（3秒）まで待つ。
        // それより早く返るなら、この状況を再現できていない（テストの前提が崩れている）。
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(2.5), $"前提が崩れている: {clock.Elapsed.TotalSeconds:0.0} 秒で返った");
        // 打ち切りの安全網が読み切りにも掛かっていること（掛かっていなければ孫の 20 秒ぶん待つ）。
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"返るまで {clock.Elapsed.TotalSeconds:0.0} 秒");
        Assert.False(result.Success);
    }
}
