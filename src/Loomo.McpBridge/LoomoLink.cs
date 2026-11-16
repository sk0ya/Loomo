using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.McpBridge;

/// <summary>
/// 橋を起こした claude の居場所（§45.3 P0b）。<see cref="Tab"/> は Loomo のターミナルが渡す環境変数（あれば早道）、
/// <see cref="Ancestors"/> は親をたどった PID の列——Loomo はこの中に自分のタブのシェルがあるかで「自分の客か」を決める。
/// </summary>
/// <para><see cref="LiveLoomos"/> は呼ぶたびに数え直す（橋は claude のセッションのあいだ生きるので、その間に Loomo が
/// 再起動して PID が変わる）。null を返せば、もう居ない Loomo の項目も外さない。</para>
internal sealed record BridgeContext(bool InsideLoomo, IReadOnlyList<int> Ancestors, string? Tab, Func<IReadOnlySet<int>?> LiveLoomos)
{
    /// <summary>環境変数とプロセスの親子から決める。どちらかが Loomo を指していれば中。</summary>
    public static BridgeContext Detect()
    {
        var tab = Environment.GetEnvironmentVariable("LOOMO_TERMINAL_TAB");
        if (string.IsNullOrWhiteSpace(tab))
            tab = null;
        try
        {
            var (ancestors, underLoomo, live) = ProcessAncestry.Snapshot();
            var first = live;
            return new BridgeContext(tab is not null || underLoomo, ancestors, tab, () =>
            {
                // 1 回目は取ったばかりの一覧を使う（フックは 1 回で終わるので数え直さない）。
                var once = Interlocked.Exchange(ref first, null);
                return once ?? ProcessAncestry.LiveLoomos();
            });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new BridgeContext(tab is not null, [], tab, static () => null);
        }
    }

    /// <summary>居場所を名乗らない（テストと、前の版の作り）。Loomo は名乗らない客を自分の客として扱う。</summary>
    public static BridgeContext Unidentified(bool insideLoomo) => new(insideLoomo, [], null, static () => null);
}

internal sealed record LoomoEndpoint(int Pid, int Port, string Token);

/// <summary>
/// Loomo が待っている場所の一覧（<c>mcp-endpoint.json</c>、§45.3 P0c）。Loomo は自分の PID ごとに 1 項目を足し、
/// 前の版の橋のために、最後に起動したものを上の段（<c>port</c>・<c>token</c>）にも書く。
/// </summary>
internal static class LoomoEndpoints
{
    /// <summary>新しい順。<paramref name="live"/> を渡せば、もう居ない Loomo の項目は外す——閉じたポートへつなぎに行くと、
    /// Windows は断られても 2 秒ほど試し続けるので、その分だけ claude が待たされる。</summary>
    public static IReadOnlyList<LoomoEndpoint> Read(string file, IReadOnlySet<int>? live)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [];
            var result = new List<LoomoEndpoint>();
            if (root.TryGetProperty("instances", out var instances) && instances.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in instances.EnumerateArray())
                {
                    if (Parse(item) is { } endpoint && (live is null || live.Contains(endpoint.Pid)))
                        result.Add(endpoint);
                }
                result.Reverse();
                return result;
            }
            return Parse(root) is { } legacy ? [legacy] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static LoomoEndpoint? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("port", out var p) || !p.TryGetInt32(out var port) || port is <= 0 or >= 65536
            || !item.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String
            || t.GetString() is not { Length: >= 16 } token || !token.All(char.IsAsciiLetterOrDigit))
            return null;
        var pid = item.TryGetProperty("pid", out var d) && d.TryGetInt32(out var n) ? n : 0;
        return new LoomoEndpoint(pid, port, token);
    }
}

/// <summary>
/// Loomo への管。行き先は呼ばれるたびにファイルから読み、居場所を名乗って順に尋ねる。421（Misdirected Request）は
/// 「その客は私のタブではない」なので次の Loomo へ。答えた Loomo を覚えて、次からはそこを先に尋ねる。
/// </summary>
internal sealed class LoomoLink : IDisposable
{
    public const string PidsHeader = "X-Loomo-Pids";
    public const string TabHeader = "X-Loomo-Tab";

    private readonly string _endpointFile;
    private readonly BridgeContext _context;
    private readonly HttpClient _http;
    private int? _owner;

    public LoomoLink(string endpointFile, BridgeContext context, TimeSpan timeout)
    {
        _endpointFile = endpointFile;
        _context = context;
        // 相手は 127.0.0.1 だけ。システムのプロキシは通さない。
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
    }

    public sealed record Response(int Status, string Body);

    /// <summary>この客を自分のものと言った Loomo の答え。どの Loomo も答えなければ null。</summary>
    public async Task<Response?> PostAsync(string path, string body, CancellationToken cancel)
    {
        IReadOnlySet<int>? live;
        try { live = _context.LiveLoomos(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { live = null; }
        var endpoints = LoomoEndpoints.Read(_endpointFile, live);
        if (_owner is { } owner)
            endpoints = endpoints.OrderBy(e => e.Pid == owner ? 0 : 1).ToList();
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}{path}")
                {
                    Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
                if (_context.Ancestors.Count > 0)
                    request.Headers.TryAddWithoutValidation(PidsHeader, string.Join(',', _context.Ancestors));
                if (_context.Tab is { } tab)
                    request.Headers.TryAddWithoutValidation(TabHeader, tab);
                using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.MisdirectedRequest)
                    continue;
                var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                _owner = endpoint.Pid;
                return new Response((int)response.StatusCode, text);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                // この Loomo は答えない（閉じた・固まった）。次へ。
            }
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
