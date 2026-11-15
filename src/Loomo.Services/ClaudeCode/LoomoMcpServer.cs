using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 部屋を Claude から使う口（§43.6 D）。Claude が<b>人間の前に物を置く</b>ための道具だけを出す——ファイルのこの行・
/// この URL・いまの頼みごとの差分。置くだけで、打たない・閉じない・配置を壊さない・フォーカスを奪わない（線引きは
/// <see cref="LoomoMcpTools"/> とホスト側）。
/// </summary>
public interface ILoomoRoomHost
{
    /// <summary>ファイルをエディタに出す（<paramref name="line"/> は 1 始まり・0 なら行は動かさない）。返すのはモデルへの一言。</summary>
    Task<LoomoRoomResult> ShowFileAsync(string path, int line);

    /// <summary>URL をブラウザペインに出す。</summary>
    Task<LoomoRoomResult> ShowUrlAsync(string url);

    /// <summary>このワークスペースで、いちばん新しい「ファイルを書き換えた頼みごと」の差分を Diff に出す。</summary>
    Task<LoomoRoomResult> ShowChangesAsync();
}

public sealed record LoomoRoomResult(bool Ok, string Message);

/// <summary>
/// Loomo の MCP サーバー（§43.6 D）。IDE 連携（§38）の口はモデルに見えるツールが 2 つに限られるので、標準の MCP
/// （Streamable HTTP・JSON 応答だけ）を別に立てる。127.0.0.1 だけで待ち、Bearer トークンの無い要求と、ブラウザからの
/// 要求（<c>Origin</c> 付き）は断る。
/// <para><b>待っている場所はファイルに書く</b>（<c>%LOCALAPPDATA%/Loomo/mcp-endpoint.json</c> のポートとトークン）。Claude Code から
/// は橋（<c>sk0ya.Loomo.McpBridge.exe</c>・標準入出力）越しに届き、橋は呼ばれるたびにこのファイルを読む。端末の環境変数では
/// 渡さない——端末のシェルは Loomo より長生きする（§34）ので、変数を持たずに生まれたシェルやポートが移ったあとのシェルから
/// つながらなくなる（2026-11-15 の不具合）。ポートとトークンは前回と同じものを使い、取られていれば空いているものへ移る。</para>
/// <para>HttpListener（http.sys）は使わず、TCP の上で HTTP を最小限だけ話す——URL 予約や管理者権限が要らない
/// （<see cref="ClaudeIdeServer"/> と同じ理由）。</para>
/// </summary>
public sealed class LoomoMcpServer : IDisposable
{
    public const string ServerName = "loomo";
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly LoomoMcpTools _tools;
    private readonly string _endpointFile;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    public LoomoMcpServer(ILoomoRoomHost host, string? endpointFile = null)
    {
        _tools = new LoomoMcpTools(host);
        _endpointFile = endpointFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "mcp-endpoint.json");
    }

    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public bool IsRunning => _listener is not null;
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    public void Start()
    {
        if (_listener is not null)
            return;
        var (savedPort, savedToken) = ReadEndpoint();
        Token = savedToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        TcpListener? listener = null;
        foreach (var port in new[] { savedPort, 0 }.Distinct())
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                break;
            }
            catch (SocketException)
            {
                listener = null; // 前のポートは取られている（別の Loomo）。空いているものへ。
            }
        }
        if (listener is null)
            throw new IOException("MCP サーバーのポートを開けませんでした。");
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WriteEndpoint();
        _ = Task.Run(() => AcceptLoopAsync(listener, _stop.Token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            _ = Task.Run(() => ServeAsync(client, stop), stop);
        }
    }

    /// <summary>接続 1 本＝要求 1 つ（返したら閉じる）。道具の呼び出しはまれで小さいので、持続接続は持たない。</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var stream = client.GetStream();
                var request = await HttpRequest.ReadAsync(stream, MaxBodyBytes, timeout.Token).ConfigureAwait(false);
                if (request is null)
                    return;
                var (status, body) = await HandleAsync(request).ConfigureAwait(false);
                await WriteResponseAsync(stream, status, body, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 途中で切れた接続は捨てる。
            }
        }
    }

    /// <summary>HTTP の要求 1 つに答える（テストから直接呼べるように分けてある）。</summary>
    internal async Task<(int Status, string? Body)> HandleAsync(HttpRequest request)
    {
        // ブラウザのページからの要求は断る（DNS リバインディング対策。claude はこのヘッダーを付けない）。
        if (request.Headers.ContainsKey("origin"))
            return (403, null);
        if (!request.Path.StartsWith("/mcp", StringComparison.Ordinal))
            return (404, null);
        if (!request.Headers.TryGetValue("authorization", out var auth) || !IsAuthorized(auth))
            return (401, null);
        // サーバーからの通知は出さないので、SSE の口（GET）もセッションの終了（DELETE）も無い。
        if (request.Method != "POST")
            return (405, null);
        JsonNode? message;
        try { message = JsonNode.Parse(request.Body); }
        catch (JsonException) { return (400, Error(null, -32700, "Parse error")); }
        if (message is not JsonObject call || call["method"]?.GetValue<string>() is not { } method)
            return (400, Error(null, -32600, "Invalid Request"));
        var id = call["id"]?.DeepClone();
        if (id is null)
            return (202, null); // 通知（notifications/initialized など）には中身を返さない
        var parameters = call["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                return (200, Result(id, new JsonObject
                {
                    // 相手の言ってきた版にそのまま合わせる（使うのは道具の一覧と呼び出しだけで、版で変わらない）。
                    ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "Loomo", ["version"] = "1" },
                    ["instructions"] = LoomoMcpTools.Instructions,
                }));
            case "ping":
                return (200, Result(id, new JsonObject()));
            case "tools/list":
                return (200, Result(id, new JsonObject { ["tools"] = LoomoMcpTools.List() }));
            case "tools/call":
                var outcome = await _tools.CallAsync(
                    parameters?["name"]?.GetValue<string>() ?? "", parameters?["arguments"] as JsonObject).ConfigureAwait(false);
                return (200, Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Message }),
                    ["isError"] = !outcome.Ok,
                }));
            default:
                return (200, Error(id, -32601, "Method not found"));
        }
    }

    private bool IsAuthorized(string header)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Token.Length == 0)
            return false;
        var given = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(Token));
    }

    private static string Result(JsonNode id, JsonObject result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();

    private static async Task WriteResponseAsync(Stream stream, int status, string? body, CancellationToken cancel)
    {
        var payload = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", _ => "Error",
        };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (payload.Length > 0)
            head.Append("Content-Type: application/json\r\n");
        if (status == 405)
            head.Append("Allow: POST\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancel).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    private (int Port, string? Token) ReadEndpoint()
    {
        try
        {
            if (!File.Exists(_endpointFile))
                return (0, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(_endpointFile));
            var port = doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) && n is > 0 and < 65536 ? n : 0;
            var token = doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString() is { Length: >= 16 } text && text.All(char.IsAsciiLetterOrDigit) ? text : null;
            return (port, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (0, null); }
    }

    private void WriteEndpoint()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_endpointFile)!);
            File.WriteAllText(_endpointFile, new JsonObject { ["port"] = Port, ["token"] = Token }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても今回は動く（次の起動でポートとトークンが変わるだけ）。
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }

    /// <summary>HTTP の要求の、ここで要る分だけ。ヘッダー名は小文字。</summary>
    internal sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public static async Task<HttpRequest?> ReadAsync(Stream stream, int maxBody, CancellationToken cancel)
        {
            var buffer = new byte[8192];
            using var received = new MemoryStream();
            var headEnd = -1;
            while (headEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
                if (read == 0)
                    return null;
                received.Write(buffer, 0, read);
                if (received.Length > 64 * 1024 + maxBody)
                    return null;
                headEnd = IndexOfHeadEnd(received.GetBuffer(), (int)received.Length);
            }
            var all = received.GetBuffer();
            var lines = Encoding.ASCII.GetString(all, 0, headEnd).Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length < 2)
                return null;
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            var length = headers.TryGetValue("content-length", out var text) && int.TryParse(text, out var n) ? n : 0;
            if (length < 0 || length > maxBody)
                return null;
            var bodyStart = headEnd + 4;
            while (received.Length - bodyStart < length)
            {
                var read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
                if (read == 0)
                    return null;
                received.Write(buffer, 0, read);
            }
            var body = Encoding.UTF8.GetString(received.GetBuffer(), bodyStart, length);
            return new HttpRequest(start[0].ToUpperInvariant(), start[1], headers, body);
        }

        private static int IndexOfHeadEnd(byte[] bytes, int length)
        {
            for (var i = 0; i + 3 < length; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    return i;
            }
            return -1;
        }
    }
}

/// <summary>
/// モデルに見せる道具（§43.6 D）。<b>置くだけ</b>の 3 つ——見えるターミナルに打つ・タブを閉じる・配置を変える・
/// ファイルを書く道具は出さない（主役は人間、§38.1）。引数の確かめ（ワークスペースの中か・http/https か）はホスト側。
/// </summary>
public sealed class LoomoMcpTools(ILoomoRoomHost host)
{
    public const string Instructions =
        "Loomo is the room (editor, browser, diff panes) the user is sitting in while talking to you. "
        + "These tools put something in front of the user so they can look at it; they never type, close or rearrange anything. "
        + "Use them sparingly, when pointing at a specific place is clearer than describing it: at most one or two per reply.";

    public static JsonArray List() =>
    [
        Tool("show_file",
            "Open a file from the user's workspace in Loomo's editor pane, optionally at a line, so the user can look at it. "
            + "Does not take keyboard focus. Use when your reply says \"look at this place\".",
            new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path, or path relative to the workspace." },
                ["line"] = new JsonObject { ["type"] = "integer", ["description"] = "1-based line to show. Optional." },
            }, "path"),
        Tool("show_url",
            "Open an http(s) URL in Loomo's browser pane for the user to read (docs, a local dev server page).",
            new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "http:// or https:// URL." },
            }, "url"),
        Tool("show_changes",
            "Open, in Loomo's diff pane, everything the most recent request changed in this workspace (before vs after, across files). "
            + "Use when you have finished editing and want the user to review.",
            new JsonObject()),
    ];

    public async Task<LoomoRoomResult> CallAsync(string name, JsonObject? arguments)
    {
        try
        {
            switch (name)
            {
                case "show_file":
                    if (Text(arguments, "path") is not { Length: > 0 } path)
                        return new LoomoRoomResult(false, "path is required.");
                    return await host.ShowFileAsync(path, Number(arguments, "line")).ConfigureAwait(false);
                case "show_url":
                    if (Text(arguments, "url") is not { Length: > 0 } url)
                        return new LoomoRoomResult(false, "url is required.");
                    return await host.ShowUrlAsync(url).ConfigureAwait(false);
                case "show_changes":
                    return await host.ShowChangesAsync().ConfigureAwait(false);
                default:
                    return new LoomoRoomResult(false, $"Unknown tool: {name}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException)
        {
            return new LoomoRoomResult(false, $"Loomo could not do that: {ex.Message}");
        }
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
    };

    private static string? Text(JsonObject? arguments, string name)
        => arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Number(JsonObject? arguments, string name)
        => arguments?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? Math.Max(0, number) : 0;
}
