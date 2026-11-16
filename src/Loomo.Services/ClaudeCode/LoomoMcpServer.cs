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

    /// <summary>面の書き方（置き場所・<c>face.json</c> の形・ページの API）。読むだけ（§44.2）。</summary>
    string FaceGuide();

    /// <summary>面を「面」ペインに出す（§44.2）。定義が読めなければ、直せる理由を返す。</summary>
    Task<LoomoRoomResult> ShowFaceAsync(string id);

    /// <summary>動きの見どころの書き方と、いまの見どころ（§44.3）。読むだけ。</summary>
    string MotionGuide();

    /// <summary>人間がいま見ているもの（前面のペイン・前に出ているファイルとカーソルの行・選択・開いているファイルの一覧）。
    /// 読むだけ・中身は選択の範囲だけ（ファイル全体は読まない）（§45.5 P2）。</summary>
    Task<LoomoRoomResult> WhatHumanSeesAsync() => Task.FromResult(new LoomoRoomResult(false, "Not available in this build."));

    /// <summary>デバッガが止まっている場所と、その場所の変数（§45.5 P2）。読むだけ。止まっていなければそう答える。</summary>
    Task<LoomoRoomResult> DebugStateAsync() => Task.FromResult(new LoomoRoomResult(false, "Not available in this build."));

    /// <summary>ブラウザのペインのページのエラー・警告（ローカルのページだけ・§45.5 P2）。読むだけ。</summary>
    Task<LoomoRoomResult> BrowserErrorsAsync() => Task.FromResult(new LoomoRoomResult(false, "Not available in this build."));

    /// <summary>
    /// 橋が名乗った居場所（環境変数のタブ・claude から上へたどった PID）から、この Loomo の客か・どのタブかを決める
    /// （§45.3 P0b・P0c）。自分のタブのシェルが祖先に無ければ null——橋は次の Loomo へ尋ねる。サーバーのスレッドから呼ばれる。
    /// </summary>
    LoomoCaller? Identify(Guid? tab, IReadOnlyList<int> ancestors) => new(tab);

    /// <summary>Claude Code のフックが 1 件届いた（§45.3 P0a）。返す JSON はそのままフックの出力になる（無ければ null）。</summary>
    Task<JsonObject?> HookAsync(LoomoCaller caller, JsonObject hookEvent) => Task.FromResult<JsonObject?>(null);
}

/// <summary>橋の向こうの claude がどのタブの中か（分からなければ <see cref="Tab"/> は null）。
/// <see cref="Nested"/> は、タブのシェルとの間に claude がもう 1 つ居る＝claude の中から起こされた claude（Bash ツールの
/// <c>claude -p</c> など）。人間が向かっている claude ではないので、フックでタブの状態を動かさない。</summary>
public sealed record LoomoCaller(Guid? Tab, bool Nested = false);

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

    private readonly ILoomoRoomHost _host;
    private readonly int _processId;
    private readonly Func<int, bool> _isLoomoAlive;

    public LoomoMcpServer(ILoomoRoomHost host, string? endpointFile = null)
        : this(host, endpointFile, Environment.ProcessId, IsLoomoProcessAlive) { }

    /// <summary>テスト用：自分の PID と「その PID の Loomo はまだ居るか」を差し替える（同じプロセスで 2 つの Loomo を演じる）。</summary>
    internal LoomoMcpServer(ILoomoRoomHost host, string? endpointFile, int processId, Func<int, bool> isLoomoAlive)
    {
        _host = host;
        _tools = new LoomoMcpTools(host);
        _processId = processId;
        _isLoomoAlive = isLoomoAlive;
        _endpointFile = endpointFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "mcp-endpoint.json");
    }

    /// <summary>橋が名乗る居場所のヘッダー（<c>sk0ya.Loomo.McpBridge</c> の <c>LoomoLink</c> と同じ名前・小文字）。</summary>
    internal const string PidsHeader = "x-loomo-pids";
    internal const string TabHeader = "x-loomo-tab";

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
        var isHook = request.Path.StartsWith("/hook", StringComparison.Ordinal);
        if (!isHook && !request.Path.StartsWith("/mcp", StringComparison.Ordinal))
            return (404, null);
        if (!request.Headers.TryGetValue("authorization", out var auth) || !IsAuthorized(auth))
            return (401, null);
        // サーバーからの通知は出さないので、SSE の口（GET）もセッションの終了（DELETE）も無い。
        if (request.Method != "POST")
            return (405, null);
        // 自分のタブの客か。違えば 421（Misdirected Request）——橋は次の Loomo へ尋ねる（Loomo を 2 つ動かしているとき）。
        if (IdentifyCaller(request) is not { } caller)
            return (421, null);
        if (isHook)
            return await HandleHookAsync(caller, request.Body).ConfigureAwait(false);
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

    /// <summary>名乗りが無い橋（前の版）は自分の客として扱う——前の版の Loomo と同じ振る舞い。</summary>
    private LoomoCaller? IdentifyCaller(HttpRequest request)
    {
        var hasPids = request.Headers.TryGetValue(PidsHeader, out var pidsText);
        var hasTab = request.Headers.TryGetValue(TabHeader, out var tabText);
        if (!hasPids && !hasTab)
            return new LoomoCaller(null);
        var tab = Guid.TryParse(tabText, out var id) ? id : (Guid?)null;
        var pids = (pidsText ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, out var pid) ? pid : 0).Where(pid => pid > 0).Take(64).ToList();
        try { return _host.Identify(tab, pids); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return null; }
    }

    private async Task<(int Status, string? Body)> HandleHookAsync(LoomoCaller caller, string body)
    {
        JsonObject? hookEvent;
        try { hookEvent = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return (400, null); }
        if (hookEvent is null)
            return (400, null);
        var output = await _host.HookAsync(caller, hookEvent).ConfigureAwait(false);
        return (200, output?.ToJsonString());
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
            404 => "Not Found", 405 => "Method Not Allowed", 421 => "Misdirected Request", _ => "Error",
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

    /// <summary>前回のポートとトークン（上の段＝最後に起動した Loomo のもの）。ポートが別の Loomo に取られていれば、開くときに移る。</summary>
    private (int Port, string? Token) ReadEndpoint()
    {
        var root = ReadEndpointFile();
        if (root is null)
            return (0, null);
        var port = root["port"] is JsonValue p && p.TryGetValue<int>(out var n) && n is > 0 and < 65536 ? n : 0;
        var token = root["token"] is JsonValue t && t.TryGetValue<string>(out var text)
            && text is { Length: >= 16 } && text.All(char.IsAsciiLetterOrDigit) ? text : null;
        return (port, token);
    }

    private JsonObject? ReadEndpointFile()
    {
        try
        {
            if (!File.Exists(_endpointFile))
                return null;
            return JsonNode.Parse(File.ReadAllText(_endpointFile)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// 待っている場所を書く（§45.3 P0c）。Loomo ごとに <c>instances</c> へ 1 項目（PID・ポート・トークン）を置き、もう居ない Loomo の
    /// 項目は外す。上の段（<c>port</c>・<c>token</c>）には最後に起動したものを書く——前の版の橋はそこだけを読む。
    /// 2 つの Loomo が同時に起動しても項目を消し合わないよう、名前付きのミューテックスの中で読み・書きする。
    /// </summary>
    private void WriteEndpoint() => UpdateEndpointFile(instances =>
    {
        instances.Add(new JsonObject { ["pid"] = _processId, ["port"] = Port, ["token"] = Token });
        return (Port, Token);
    });

    /// <summary>閉じるときに自分の項目を外す。上の段は残った Loomo の最後のものへ（居なければ自分のまま＝次の起動で同じポートを使う）。</summary>
    private void RemoveEndpoint() => UpdateEndpointFile(instances =>
    {
        if (instances.LastOrDefault() is JsonObject last
            && last["port"] is JsonValue p && p.TryGetValue<int>(out var port)
            && last["token"] is JsonValue t && t.TryGetValue<string>(out var token) && token is not null)
            return (port, token);
        return (Port, Token);
    });

    private void UpdateEndpointFile(Func<JsonArray, (int Port, string Token)> change)
    {
        using var mutex = new Mutex(false, @"Local\loomo-mcp-endpoint");
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { owned = true; }
            var instances = new JsonArray();
            if (ReadEndpointFile()?["instances"] is JsonArray existing)
            {
                foreach (var item in existing.OfType<JsonObject>())
                {
                    if (item["pid"] is JsonValue v && v.TryGetValue<int>(out var pid) && pid != _processId && _isLoomoAlive(pid))
                        instances.Add(item.DeepClone());
                }
            }
            var (port, token) = change(instances);
            Directory.CreateDirectory(Path.GetDirectoryName(_endpointFile)!);
            var temp = _endpointFile + "." + _processId + ".tmp";
            File.WriteAllText(temp, new JsonObject { ["port"] = port, ["token"] = token, ["instances"] = instances }.ToJsonString());
            File.Move(temp, _endpointFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても今回は動く（次の起動でポートとトークンが変わるだけ）。
        }
        finally
        {
            if (owned)
                mutex.ReleaseMutex();
        }
    }

    private static bool IsLoomoProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited
                   && string.Equals(process.ProcessName, "sk0ya.Loomo.App", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_listener is not null)
            RemoveEndpoint();
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
/// モデルに見せる道具（§43.6 D・§44.2）。<b>置く</b>道具（ファイル・URL・差分・面）と、面の書き方を<b>読む</b>道具だけ
/// ——見えるターミナルに打つ・タブを閉じる・配置を変える・ファイルを書く道具は出さない（主役は人間、§38.1。面のファイルを
/// 書くのは Claude Code 自身の道具）。引数の確かめ（ワークスペースの中か・http/https か）はホスト側。
/// </summary>
public sealed class LoomoMcpTools(ILoomoRoomHost host)
{
    public const string Instructions =
        "Loomo is the room (editor, browser, diff panes) the user is sitting in while talking to you. "
        + "These tools put something in front of the user so they can look at it; they never type, close or rearrange anything. "
        + "Use them sparingly, when pointing at a specific place is clearer than describing it: at most one or two per reply. "
        + "When the user asks for a small tool, view or dashboard inside the room, grow a \"face\": call face_guide, write the files it describes, then call show_face.";

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
        Tool("face_guide",
            "Read how to grow a \"face\": a small HTML tool that lives in a pane of Loomo and can call the room (open files, read the workspace, "
            + "run declared commands). Returns the folder to write into, the face.json format, the page API and the existing faces. "
            + "Call this before writing a face. Read-only.",
            new JsonObject()),
        Tool("show_face",
            "Show a face (written as described by face_guide) in Loomo's face pane. Does not take keyboard focus. "
            + "If face.json cannot be read, the result says what to fix. A face that declares run, net, motion or orders waits for the user's one-time approval before it opens; others open at once.",
            new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "The face id (its folder name)." },
            }, "id"),
        Tool("debug_state",
            "Read the debugger in Loomo: whether it is paused, where (the call stack, top frames) and the variables of the selected frame "
            + "(values cut short). Read-only. Call it when a debug session is paused and you need to know the state.",
            new JsonObject()),
        Tool("browser_errors",
            "Read the errors and warnings the page in Loomo's browser pane has logged since Loomo started watching it "
            + "(uncaught exceptions, console.error, console.warn), with the URL and line. Only for local pages (localhost, Loomo's files, "
            + "workspace files); other sites are not read. Read-only. Call it when the user says the page is broken or shows errors.",
            new JsonObject()),
        Tool("what_human_sees",
            "Describe what the user is looking at right now in Loomo: the front pane, the file in the editor with the caret line, "
            + "the selected text (only the selection), whether it has unsaved changes, and the other open files. Read-only. "
            + "Call it when the user says \"this\", \"here\" or \"what I'm looking at\" and you need to see it.",
            new JsonObject()),
        Tool("motion_guide",
            "Read how Loomo records the workspace's \"motion\" (command output, test outcomes, pictures) after each of your turns, "
            + "where the probes file is and which probes exist. Call it when the user asks to watch a build, tests or a screen as a probe. Read-only.",
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
                case "face_guide":
                    return new LoomoRoomResult(true, host.FaceGuide());
                case "motion_guide":
                    return new LoomoRoomResult(true, host.MotionGuide());
                case "what_human_sees":
                    return await host.WhatHumanSeesAsync().ConfigureAwait(false);
                case "debug_state":
                    return await host.DebugStateAsync().ConfigureAwait(false);
                case "browser_errors":
                    return await host.BrowserErrorsAsync().ConfigureAwait(false);
                case "show_face":
                    if (Text(arguments, "id") is not { Length: > 0 } face)
                        return new LoomoRoomResult(false, "id is required.");
                    return await host.ShowFaceAsync(face).ConfigureAwait(false);
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
