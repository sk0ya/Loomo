using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Pty.Host;

// 端末の常駐ホスト（設計 §34）。名前付きパイプでセッションの作成・再接続を受け、ConPTY とシェルを
// Loomo 本体より長く生かしておく（落ちた Loomo の再起動で戻れるように）。最後の Loomo が正常に終わったら
// シェルを全部殺して終わる。セッションが無く誰も繋がっていない状態がしばらく続いたときも自分で終わる。
//
// 同じユーザー・同じログオンセッションに1つだけ。2つ目に起こされたら何もせず終わる（先にいる方が答える）。

var pipeName = PtyProtocol.PipeName();
using var single = new Mutex(initiallyOwned: true, PtyProtocol.HostMutexName(pipeName), out bool owned);
if (!owned)
    return 0;

HostLog.Write($"起動 pid={Environment.ProcessId} pipe={pipeName} dir={AppContext.BaseDirectory}");
try
{
    new PtyHostServer(pipeName, idleExit: TimeSpan.FromMinutes(1), lastClientLeft: () => Environment.Exit(0),
        archiveDirectory: TerminalArchive.DefaultDirectory).Run();
    return 0;
}
catch (Exception ex)
{
    HostLog.Write($"異常終了: {ex}");
    return 1;
}
