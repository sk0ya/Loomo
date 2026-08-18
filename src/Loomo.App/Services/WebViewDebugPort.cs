using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>可視ブラウザペイン（WebView2）を js-debug から CDP アタッチできるようにするためのリモートデバッグポート。
/// WebView2 は UserDataFolder を共有する全コントロールが<b>同一のブラウザ引数</b>である必要があるため、
/// 生成プロパティ（<c>CreateWebViewCreationProperties</c>）に一度だけ <c>--remote-debugging-port=&lt;N&gt;</c> を付け、
/// 全ペインが 1 つの共有ブラウザプロセス＝1 つの CDP エンドポイント（127.0.0.1:N のみ）を露出する。
/// フロントデバッグ（TS IDE）はこのポートへ <c>pwa-chrome</c> の attach を張り、ペインそのものをデバッグする。
///
/// <para><b>Loomo を2つ起動しても壊さないこと</b>——「同一引数」の縛りはプロセスをまたいでも効く。素朴に
/// 「空きポートを選ぶ」だけだと、2つ目は先行インスタンスのブラウザプロセスが 9333 を listen しているせいで
/// 9334 を選び、同じ UserDataFolder に<b>違う引数</b>で入ることになって環境生成が
/// <c>ERROR_INVALID_STATE (0x8007139F)</c> で失敗する＝ブラウザペインと EditorSupport が丸ごと無反応になる。
/// そこで選んだポートを控え（<see cref="WebViewProfile.DebugPortRecord"/>）に残し、先行インスタンスが生きていれば
/// 空きでなくても<b>その番号を引き継ぐ</b>。空きポート探索（dev サーバーとの衝突回避）は初回だけ働く。</para></summary>
internal static class WebViewDebugPort
{
    /// <summary>探索の起点（一般的な dev ポートと衝突しにくい帯）。</summary>
    private const int StartPort = 9333;

    private static readonly object Gate = new();
    private static int? _port;

    /// <summary>CDP ポート（プロセスにつき一度だけ決める）。localhost にのみバインドされる。</summary>
    public static int Port
    {
        get { lock (Gate) return _port ??= Resolve(WebViewProfile.DebugPortRecord, IsPortListening, IsOwnerAlive, () => DevServerPortUtil.FindFreePort(StartPort)); }
    }

    /// <summary>WebView2 生成引数に足すリモートデバッグ指定。</summary>
    public static string Argument => $"--remote-debugging-port={Port}";

    /// <summary>控えの中身（どのプロセスがどの番号を選んだか）。</summary>
    internal readonly record struct Record(int OwnerProcessId, int Port);

    /// <summary>使う番号を決める。控えがあり、かつ<b>その番号がまだ生きている</b>（誰かが listen している、
    /// または控えを書いたインスタンスが動いている）なら引き継ぐ。そうでなければ空きを選んで控えを更新する。
    /// 判定を引数で受けるのは、テストから実プロセス・実ソケット無しで確かめられるようにするため。</summary>
    internal static int Resolve(string recordPath, Func<int, bool> portListening, Func<int, bool> ownerAlive, Func<int> pickFreePort)
    {
        if (TryReadRecord(recordPath, out var record) && (portListening(record.Port) || ownerAlive(record.OwnerProcessId)))
            return record.Port;
        var port = pickFreePort();
        TryWriteRecord(recordPath, new Record(Environment.ProcessId, port));
        return port;
    }

    /// <summary>控えを読む（形式は <c>"&lt;pid&gt; &lt;port&gt;"</c> の1行）。壊れていれば無かったことにする。</summary>
    internal static bool TryReadRecord(string path, out Record record)
    {
        record = default;
        try
        {
            if (!File.Exists(path))
                return false;
            var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var port))
                return false;
            if (port is <= 0 or > 65535)
                return false;
            record = new Record(pid, port);
            return true;
        }
        catch { return false; }
    }

    /// <summary>控えを書く。失敗しても致命的ではない（次のインスタンスが選び直すだけ）。</summary>
    internal static void TryWriteRecord(string path, Record record)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(path, $"{record.OwnerProcessId} {record.Port}");
        }
        catch { }
    }

    /// <summary>その番号を誰かが listen しているか＝共有ブラウザプロセスがまだ居るか。
    /// 控えを書いたインスタンスが先に落ちても、WebView2 を握っている別インスタンスが居れば引き継げる。</summary>
    private static bool IsPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address));
        }
        catch { return false; }
    }

    /// <summary>控えを書いたインスタンスがまだ動いているか。ブラウザプロセスが立ち上がる前
    /// （＝まだ listen していない）に2つ目が起動しても引き継げるようにするための判定。
    /// pid の使い回しを踏まないよう、プロセス名が自分と同じことまで確かめる。</summary>
    private static bool IsOwnerAlive(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
            return false;   // 自分の pid が控えに載っているのは前回の残骸（使い回し）＝生きていない扱い
        try
        {
            using var owner = Process.GetProcessById(processId);
            return !owner.HasExited
                && string.Equals(owner.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
