using System.Net;
using System.Net.NetworkInformation;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>可視ブラウザペイン（WebView2）を js-debug から CDP アタッチできるようにするためのリモートデバッグポート。
/// WebView2 は UserDataFolder を共有する全コントロールが<b>同一のブラウザ引数</b>である必要があるため、
/// 生成プロパティ（<see cref="WebViewEnvironment.CreateProperties"/>）に一度だけ <c>--remote-debugging-port=&lt;N&gt;</c>
/// を付け、全ペインが 1 つの共有ブラウザプロセス＝1 つの CDP エンドポイント（ループバックのみ）を露出する。
/// フロントデバッグ（TS IDE）はこのポートへ <c>pwa-chrome</c> の attach を張り、ペインそのものをデバッグする。
///
/// <para><b>Loomo を2つ起動しても壊さないこと</b>——「同一引数」の縛りはプロセスをまたいでも効く。番号が食い違うと
/// 環境生成が <c>ERROR_INVALID_STATE (0x8007139F)</c> で失敗し、負けた側のブラウザペインと EditorSupport が
/// 丸ごと無反応になる（ヘッダーだけ更新されて中身が永久に描かれない）。そこで控え
/// （<see cref="WebViewProfile.DebugPortRecord"/>）を<b>生きているインスタンスの申告（claim）の一覧</b>にし、
/// 誰か1人でも生きていれば<b>必ずその番号に合わせる</b>。空きポート探索（dev サーバーとの衝突回避、§29）が
/// 働くのは誰も居ないときだけ。</para>
///
/// <para><b>なぜ「持ち主1人」ではなく一覧なのか</b>——控えを1行（持ち主 pid ＋番号）にすると、
/// <b>引き継いだインスタンスは持ち主にならない</b>。持ち主が先に終了し、引き継いだ側がまだ WebView2 を
/// 実体化していない（＝まだ listen していない。ペインは遅延実体化なので数分空くことがある）と、次に起動した
/// インスタンスからは「誰も居ない」ように見えて別の空き番号を選んでしまう——これが実機で起きていた食い違いの筋道。
/// 全員が自分の claim を書き足せば、生きているインスタンスの番号は必ず見える。</para></summary>
internal static class WebViewDebugPort
{
    /// <summary>探索の起点（一般的な dev ポートと衝突しにくい帯）。</summary>
    private const int StartPort = 9333;

    /// <summary>探索・引き当て直しで見る帯の幅。</summary>
    private const int SearchSpan = 100;

    private static readonly object Gate = new();
    private static int? _port;
    private static bool _adopted;

    /// <summary>CDP ポート（プロセスにつき一度だけ決める）。ループバックにのみバインドされる。</summary>
    public static int Port
    {
        get { lock (Gate) return _port ??= ClaimPort(); }
    }

    /// <summary>WebView2 生成引数に足すリモートデバッグ指定。</summary>
    public static string Argument => $"--remote-debugging-port={Port}";

    /// <summary>控えの1行＝「この pid がこの番号を使っている」という申告。</summary>
    internal readonly record struct Claim(int ProcessId, int Port);

    /// <summary>使う番号を決め、控えに残す claim の一覧を返す。<b>生きている claim があれば必ずそれに合わせる</b>
    /// （空いていなくても、空いていても）。生きている＝そのプロセスが動いている、またはその番号を誰かが listen して
    /// いる（＝控えの主が落ちた後も共有ブラウザプロセスが残っている場合）。判定を引数で受けるのは、テストから
    /// 実プロセス・実ソケット無しで確かめられるようにするため。</summary>
    internal static (int Port, IReadOnlyList<Claim> Claims) Resolve(
        IEnumerable<Claim> existing,
        Func<int, bool> processAlive,
        Func<int, bool> portListening,
        Func<int> pickFreePort,
        int ownProcessId)
    {
        var live = new List<Claim>();        // まだ動いているインスタンスの申告
        var lingering = new List<Claim>();   // 主は終了したが、その番号はまだ listen 中（＝共有ブラウザが残っている）
        foreach (var claim in existing)
        {
            // 自分の pid が載っているのは前回の残骸（pid の使い回し）＝生きていない扱い。
            if (claim.ProcessId == ownProcessId
                || live.Any(c => c.ProcessId == claim.ProcessId)
                || lingering.Any(c => c.ProcessId == claim.ProcessId))
                continue;
            if (processAlive(claim.ProcessId))
                live.Add(claim);
            // 居残りは番号につき1つでよい（残す意味は「その番号がまだ握られている」だけ）。
            else if (portListening(claim.Port) && lingering.All(c => c.Port != claim.Port))
                lingering.Add(claim);
        }

        // いま実際にブラウザプロセスが握っている番号を最優先（申告と実体がずれていたら実体が正しい）。
        var port = live.FirstOrDefault(c => portListening(c.Port)) is { Port: > 0 } running ? running.Port
            : live.Count > 0 ? live[0].Port
            : lingering.Count > 0 ? lingering[0].Port
            : pickFreePort();

        // 残す申告は「生きているプロセス」＋「生きた申告が無い番号を握っている居残り」だけ。
        // 居残りを無条件に残すと、番号が listen され続けるかぎり死んだ pid が溜まり続ける。
        var claims = new List<Claim>(live);
        claims.AddRange(lingering.Where(c => live.All(l => l.Port != c.Port)));
        claims.Add(new Claim(ownProcessId, port));
        return (port, claims);
    }

    /// <summary>環境生成が失敗した＝同じプロファイルを<b>別の引数の</b>ブラウザプロセスが握っている。
    /// いま listen している番号へ合わせ直す（引き当て直しは1プロセス1回きり）。変えられたら true＝
    /// 呼び元は WebView2 を作り直して再試行してよい。</summary>
    public static bool TryAdoptRunningPort()
    {
        lock (Gate)
        {
            if (_adopted)
                return false;
            var current = _port ??= ClaimPort();
            if (SelectRunningPort(ReadClaims(), ListeningPorts(), current) is not { } running)
                return false;
            _adopted = true;
            _port = running;
            Reclaim(running);
            return true;
        }
    }

    /// <summary>いま動いている共有ブラウザプロセスの番号を選ぶ。控えに載っている番号のうち listen 中のものを
    /// 優先し（＝Loomo が使ったことのある番号）、無ければ探索帯で listen している最小の番号を採る。</summary>
    internal static int? SelectRunningPort(IEnumerable<Claim> claims, IReadOnlySet<int> listening, int? current)
    {
        foreach (var claim in claims)
            if (claim.Port != current && listening.Contains(claim.Port))
                return claim.Port;
        for (var port = StartPort; port < StartPort + SearchSpan; port++)
            if (port != current && listening.Contains(port))
                return port;
        return null;
    }

    /// <summary>控えを読む（1行 <c>"&lt;pid&gt; &lt;port&gt;"</c> の並び。壊れた行は無かったことにする）。</summary>
    internal static IReadOnlyList<Claim> ParseClaims(string? text)
    {
        var claims = new List<Claim>();
        if (string.IsNullOrWhiteSpace(text))
            return claims;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var port))
                continue;
            if (pid <= 0 || port is <= 0 or > 65535)
                continue;
            claims.Add(new Claim(pid, port));
        }
        return claims;
    }

    internal static string FormatClaims(IEnumerable<Claim> claims)
        => string.Join("\n", claims.Select(c => $"{c.ProcessId} {c.Port}"));

    /// <summary>控えを読み取り、自分の claim を書き足して番号を決める。読み書きは<b>排他で</b>行う——
    /// 2つ同時に起動したとき、両方が「誰も居ない」を見てから別々に書くと食い違うため。</summary>
    private static int ClaimPort()
    {
        try
        {
            var folder = Path.GetDirectoryName(WebViewProfile.DebugPortRecord);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            using var stream = OpenExclusive(WebViewProfile.DebugPortRecord);
            if (stream is null)
                return PickFreePort();   // 控えを掴めなくても起動は止めない（最悪は従来どおりの空き探索）
            var (port, claims) = Resolve(
                ParseClaims(ReadAll(stream)), IsProcessAlive, IsPortListening, PickFreePort, Environment.ProcessId);
            Overwrite(stream, FormatClaims(claims));
            return port;
        }
        catch { return PickFreePort(); }
    }

    /// <summary>引き当て直した番号で自分の claim を書き替える。失敗しても致命的ではない。</summary>
    private static void Reclaim(int port)
    {
        try
        {
            using var stream = OpenExclusive(WebViewProfile.DebugPortRecord);
            if (stream is null)
                return;
            var claims = ParseClaims(ReadAll(stream))
                .Where(c => c.ProcessId != Environment.ProcessId)
                .Append(new Claim(Environment.ProcessId, port));
            Overwrite(stream, FormatClaims(claims));
        }
        catch { }
    }

    private static IReadOnlyList<Claim> ReadClaims()
    {
        try { return ParseClaims(File.ReadAllText(WebViewProfile.DebugPortRecord)); }
        catch { return Array.Empty<Claim>(); }
    }

    /// <summary>控えを排他で開く（他インスタンスが書いている最中なら少し待つ）。</summary>
    private static FileStream? OpenExclusive(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    private static string ReadAll(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static void Overwrite(FileStream stream, string text)
    {
        stream.Position = 0;
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(text);
    }

    private static int PickFreePort() => DevServerPortUtil.FindFreePort(StartPort);

    /// <summary>ループバックで listen 中のポート。</summary>
    private static IReadOnlySet<int> ListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch { return new HashSet<int>(); }
    }

    /// <summary>その番号を誰かが listen しているか＝共有ブラウザプロセスがまだ居るか。
    /// 控えを書いたインスタンスが先に落ちても、WebView2 を握っている別インスタンスが居れば引き継げる。
    /// WebView2 の CDP は環境によって <c>::1</c> にだけ bind するので、IPv4／IPv6 を区別せず見る。</summary>
    private static bool IsPortListening(int port) => ListeningPorts().Contains(port);

    /// <summary>その claim を書いたインスタンスがまだ動いているか。ブラウザプロセスが立ち上がる前
    /// （＝まだ listen していない）に2つ目が起動しても合わせられるようにするための判定。
    /// pid の使い回しを踏まないよう、プロセス名が自分と同じことまで確かめる。</summary>
    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
            return false;
        try
        {
            using var other = Process.GetProcessById(processId);
            return !other.HasExited
                && string.Equals(other.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
