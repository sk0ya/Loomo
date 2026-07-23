using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>フロント開発サーバー複合起動（Phase B / P2）のポート補助。可視ターミナルの出力は購読できないため、
/// 「こちらが空きポートを選んで固定注入 → そのポートが listen するまで TCP で待つ」で URL を決定論化する。</summary>
internal static class DevServerPortUtil
{
    /// <summary><paramref name="start"/> から昇順で最初の空き TCP ポート（ループバックに bind できるもの）を返す。
    /// 見つからなければ <paramref name="start"/> を返す（起動側の strictPort 失敗で気付ける）。</summary>
    public static int FindFreePort(int start)
    {
        for (var port = start; port < start + 100 && port <= 65535; port++)
            if (IsPortFree(port)) return port;
        return start;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            // ループバックに bind できれば空き。TIME_WAIT の取り合いを避けるため ExclusiveAddressUse=true。
            using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>ポートが listen を開始する（＝サーバーが立ち上がる）まで待つ。<paramref name="timeout"/> 内に
    /// 接続できれば true。開発サーバーは通常 1 秒未満で立つが、初回依存解決を見込んで既定 30 秒。</summary>
    public static async Task<bool> WaitUntilListeningAsync(int port, int timeoutMs, CancellationToken ct)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        while (System.Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await CanConnectAsync(port, ct)) return true;
            try { await Task.Delay(250, ct); } catch (TaskCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>ポートが今 listen しているか（サーバー再利用判定用の 1 回きりの接続試行）。</summary>
    public static Task<bool> IsListeningAsync(int port, CancellationToken ct) => CanConnectAsync(port, ct);

    private static async Task<bool> CanConnectAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
