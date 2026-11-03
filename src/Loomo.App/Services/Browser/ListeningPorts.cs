namespace sk0ya.Loomo.App.Services;

/// <summary>
/// TCP のポートで待ち受けているプロセスの PID（ブラウザサポート §41：localhost のページ → それを動かしているターミナルのタブ）。
/// <c>netstat -ano</c> を起こして読む代わりに、同じ表を <c>GetExtendedTcpTable</c> で直接引く（子プロセスを起こさない・速い）。
/// IPv4 と IPv6 の両方を見る（Vite は既定で <c>::1</c> だけで待ち受ける）。
/// </summary>
internal static class ListeningPorts
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>そのポートで待ち受けているプロセス（無ければ空）。</summary>
    public static IReadOnlyList<int> OwnersOf(int port)
    {
        var owners = new List<int>();
        Collect(AfInet, rowSize: 24, portOffset: 8, pidOffset: 20, port, owners);
        Collect(AfInet6, rowSize: 56, portOffset: 20, pidOffset: 52, port, owners);
        return owners.Distinct().ToList();
    }

    private static void Collect(int family, int rowSize, int portOffset, int pidOffset, int port, List<int> owners)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0)
            return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
                return;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                if (NetworkPort(Marshal.ReadInt32(row + portOffset)) == port)
                    owners.Add(Marshal.ReadInt32(row + pidOffset));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>表のポートはネットワークバイト順で下位 16 ビットに入っている。</summary>
    internal static int NetworkPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
