using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 子プロセスの出力読み取りが<b>スレッドプールを使わない</b>ことを固定する。
/// ここが崩れると（<c>ReadToEndAsync</c>／<c>BeginOutputReadLine</c> へ戻すと）読み手が
/// 子プロセスの寿命ぶんワーカーを抱え、起動直後に端末のプロンプトが数秒出なくなる——
/// 2026-09-24 に実際に起きた形なので、速度ではなく<b>誰のスレッドで読むか</b>を検査する。
/// </summary>
public sealed class ChildProcessIoTests
{
    private static Process StartCmd(string command)
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c " + command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        return process!;
    }

    [Fact]
    public async Task 標準出力を読み切る()
    {
        using var process = StartCmd("echo 部屋");

        var stdout = await ChildProcessIo.ReadToEndAsync(process.StandardOutput, "テスト:stdout");

        Assert.Contains("部屋", stdout.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 読み取りはプールのスレッドで走らない()
    {
        using var process = StartCmd("echo 1& echo 2");
        var pooled = new ConcurrentBag<bool>();

        await ChildProcessIo.PumpLinesAsync(
            process.StandardOutput,
            _ => pooled.Add(Thread.CurrentThread.IsThreadPoolThread),
            "テスト:pump");

        Assert.NotEmpty(pooled);
        Assert.DoesNotContain(true, pooled);
    }

    [Fact]
    public async Task 行は順に届き終端で完了する()
    {
        using var process = StartCmd("echo a& echo b& echo c");
        var lines = new ConcurrentQueue<string>();

        await ChildProcessIo.PumpLinesAsync(process.StandardOutput, lines.Enqueue, "テスト:pump");

        Assert.Equal(new[] { "a", "b", "c" }, lines.Select(l => l.Trim()).ToArray());
    }

    [Fact]
    public async Task 読み取りは名前つきの専用スレッドで走る()
    {
        using var process = StartCmd("echo x");

        // スレッドの素性は**走っている最中**に採る（読み終えたスレッドは既に死んでいて覗けない）。
        (bool Pooled, string? Name, bool Background, int Id) reader = default;
        await ChildProcessIo.RunOffPoolAsync("テスト:専用スレッド", () =>
        {
            var t = Thread.CurrentThread;
            reader = (t.IsThreadPoolThread, t.Name, t.IsBackground, t.ManagedThreadId);
            return process.StandardOutput.ReadToEnd();
        });

        Assert.False(reader.Pooled);                        // プールの椅子を取らない
        Assert.Equal("テスト:専用スレッド", reader.Name);    // どのスレッドが何を読んでいるか名前で分かる
        Assert.True(reader.Background);                     // 読み手が残ってもアプリの終了を止めない
        // 待っていた側の続きは読み取りスレッドへ引き込まれない（引き込むと次の読みが始まらない）。
        Assert.NotEqual(reader.Id, Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task 殺されたプロセスの読み取りは終端で返る()
    {
        // 打ち切りは「殺してパイプを閉じる」——読み手を置き去りにしない。
        var process = StartCmd("ping -n 30 127.0.0.1 > nul");
        var read = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "テスト:stdout");

        process.Kill(entireProcessTree: true);

        var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));
        process.Dispose();
        Assert.Same(read, completed);
    }
}
