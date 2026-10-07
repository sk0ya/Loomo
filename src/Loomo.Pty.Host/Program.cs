using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Pty.Host;

// 端末の常駐ホスト（設計 §34）。名前付きパイプでセッションの作成・再接続を受け、ConPTY とシェルを
// Loomo 本体より長く生かしておく。セッションが無く誰も繋がっていない状態がしばらく続いたら自分で終わる。
//
// 同じユーザー・同じログオンセッションに1つだけ。2つ目に起こされたら何もせず終わる（先にいる方が答える）。

var pipeName = PtyProtocol.PipeName();
using var single = new Mutex(initiallyOwned: true, $@"Local\{pipeName}-host", out bool owned);
if (!owned)
    return 0;

HostLog.Write($"起動 pid={Environment.ProcessId} pipe={pipeName} dir={AppContext.BaseDirectory}");
try
{
    new PtyHostServer(pipeName, idleExit: TimeSpan.FromMinutes(1)).Run();
    return 0;
}
catch (Exception ex)
{
    HostLog.Write($"異常終了: {ex}");
    return 1;
}
