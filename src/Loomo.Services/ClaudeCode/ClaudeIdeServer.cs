using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// Claude Code の IDE 連携サーバー（§38）。Claude Code は VS Code / JetBrains の拡張とこの形で話す：
/// IDE が 127.0.0.1 で WebSocket を待ち、<c>~/.claude/ide/&lt;port&gt;.lock</c> にポート・認証トークン・作業フォルダーを
/// 書く。端末に <c>CLAUDE_CODE_SSE_PORT</c> と <c>ENABLE_IDE_INTEGRATION=true</c> があれば、そこで起動した claude は
/// lock を読んで自分から繋いでくる。中身は MCP（JSON-RPC 2.0）で、IDE はツール（<see cref="ClaudeIdeTools"/>）を
/// 答え、選択が変わったら <c>selection_changed</c> を送る。
/// <para><b>ポートは前回と同じものを先に試す</b>。端末のシェルは Loomo より長生きする（§34 の常駐セッション）ので、
/// 再起動後も古いシェルの環境変数が指すポートで待てるように。取られていれば空いているポートへ移る。</para>
/// <para>WebSocket は http.sys（HttpListener）を使わず、TCP の上で握手だけ自前で行い
/// <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/> に渡す——URL 予約や管理者権限が要らない。</para>
/// </summary>
public sealed class ClaudeIdeServer : IDisposable
{
    private const string AuthHeader = "x-claude-code-ide-authorization";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly ClaudeIdeTools _tools;
    private readonly Func<IReadOnlyList<string>> _workspaceFolders;
    private readonly string _ideDirectory;
    private readonly string _portFile;
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private string? _lockPath;

    public ClaudeIdeServer(IClaudeIdeHost host, Func<IReadOnlyList<string>> workspaceFolders,
        string? claudeConfigDirectory = null, string? portFile = null)
    {
        _workspaceFolders = workspaceFolders;
        _tools = new ClaudeIdeTools(host, workspaceFolders);
        _ideDirectory = Path.Combine(claudeConfigDirectory ?? DefaultClaudeConfigDirectory(), "ide");
        _portFile = portFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "claude-ide-port");
        AuthToken = Guid.NewGuid().ToString();
    }

    public int Port { get; private set; }
    public string AuthToken { get; }
    public bool IsRunning => _listener is not null;

    /// <summary>接続している claude の数が変わった（UI スレッドとは限らない）。</summary>
    public event EventHandler? ConnectionsChanged;
    public int ConnectionCount => _connections.Count;

    /// <summary>送受信した JSON-RPC の生の文字列（診断用。→ は受信、← は送信）。</summary>
    public event Action<string>? MessageTraced;

    /// <summary>端末に渡す環境変数。そこで起動した claude がこのサーバーへ自動で繋ぐ。</summary>
    public IReadOnlyDictionary<string, string> TerminalEnvironment => IsRunning
        ? new Dictionary<string, string>
        {
            ["CLAUDE_CODE_SSE_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ENABLE_IDE_INTEGRATION"] = "true",
        }
        : new Dictionary<string, string>();

    public static string DefaultClaudeConfigDirectory()
        => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public void Start()
    {
        if (_listener is not null)
            return;
        _listener = Listen(ReadSavedPort());
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        TrySavePort(Port);
        WriteLockFile();
        _ = AcceptLoopAsync(_listener, _stop.Token);
    }

    /// <summary>作業フォルダーが変わったら lock を書き直す（Claude の <c>/ide</c> はこれで候補を絞る）。</summary>
    public void UpdateWorkspaceFolders()
    {
        if (IsRunning)
            WriteLockFile();
    }

    /// <summary>人間の選択を Claude へ知らせる。繋がっていなければ覚えるだけ。</summary>
    public void PublishSelection(ClaudeIdeSelection selection)
    {
        _tools.RememberSelection(selection);
        Broadcast("selection_changed", ClaudeIdeTools.SelectionParams(selection));
    }

    /// <summary>人間が「この場所を見て」と明示的に渡した（Claude のプロンプトに @参照 が入る）。行は 0 始まり。</summary>
    public bool PublishAtMention(string filePath, int? lineStart, int? lineEnd)
    {
        var parameters = new JsonObject { ["filePath"] = filePath };
        if (lineStart is { } start)
        {
            parameters["lineStart"] = start;
            parameters["lineEnd"] = lineEnd ?? start;
        }
        return Broadcast("at_mentioned", parameters) > 0;
    }

    private int Broadcast(string method, JsonObject parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }.ToJsonString(ClaudeIdeTools.JsonOptions);
        var sent = 0;
        foreach (var connection in _connections.Keys)
        {
            _ = connection.SendAsync(message);
            sent++;
        }
        return sent;
    }

    private static TcpListener Listen(int? preferredPort)
    {
        if (preferredPort is { } port)
        {
            try
            {
                var preferred = new TcpListener(IPAddress.Loopback, port);
                preferred.Start();
                return preferred;
            }
            catch (SocketException)
            {
                // 取られている（別の Loomo・別のアプリ）。空いているポートへ。
            }
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private int? ReadSavedPort()
    {
        try
        {
            return int.TryParse(File.ReadAllText(_portFile).Trim(), out var port) && port is > 1024 and < 65536 ? port : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void TrySavePort(int port)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_portFile)!);
            File.WriteAllText(_portFile, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WriteLockFile()
    {
        var folders = new JsonArray();
        foreach (var folder in _workspaceFolders())
            folders.Add(folder);
        var content = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["workspaceFolders"] = folders,
            ["ideName"] = "Loomo",
            ["transport"] = "ws",
            ["runningInWindows"] = true,
            ["authToken"] = AuthToken,
        };
        try
        {
            Directory.CreateDirectory(_ideDirectory);
            _lockPath = Path.Combine(_ideDirectory, $"{Port}.lock");
            File.WriteAllText(_lockPath, content.ToJsonString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var owned = client;
        var stream = client.GetStream();
        WebSocket? socket;
        try { socket = await HandshakeAsync(stream, AuthToken, cancellationToken); }
        catch (Exception) { return; }
        if (socket is null)
            return;

        var connection = new Connection(socket);
        _connections[connection] = 0;
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await ReceiveLoopAsync(connection, cancellationToken);
        }
        catch (Exception) { }
        finally
        {
            _connections.TryRemove(connection, out _);
            connection.Dispose();
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>HTTP の Upgrade を受けて WebSocket にする。トークンが合わなければ 401 で切る。</summary>
    internal static async Task<WebSocket?> HandshakeAsync(Stream stream, string authToken, CancellationToken cancellationToken)
    {
        var headers = await ReadHeadersAsync(stream, cancellationToken);
        if (headers is null)
            return null;
        if (!headers.TryGetValue(AuthHeader, out var token) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(authToken)))
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellationToken);
            return null;
        }
        if (!headers.TryGetValue("sec-websocket-key", out var key))
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellationToken);
            return null;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
        var response = new StringBuilder()
            .Append("HTTP/1.1 101 Switching Protocols\r\n")
            .Append("Upgrade: websocket\r\n")
            .Append("Connection: Upgrade\r\n")
            .Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
        string? subProtocol = null;
        if (headers.TryGetValue("sec-websocket-protocol", out var protocols))
        {
            subProtocol = protocols.Split(',')[0].Trim();
            response.Append("Sec-WebSocket-Protocol: ").Append(subProtocol).Append("\r\n");
        }
        response.Append("\r\n");
        await WriteAsciiAsync(stream, response.ToString(), cancellationToken);
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = subProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
    }

    private static async Task<Dictionary<string, string>?> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (read == 0)
                return null;
            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n'
                && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                break;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal))
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken).AsTask();

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (connection.Socket.State == WebSocketState.Open)
        {
            var result = await connection.Socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            MessageTraced?.Invoke("→ " + text);
            // ツール呼び出し（openDiff）は人間を待って長く止まるので、受信を塞がないよう切り離して答える。
            _ = Task.Run(async () =>
            {
                if (await HandleAsync(text, connection.Closed) is { } reply)
                {
                    MessageTraced?.Invoke("← " + reply);
                    await connection.SendAsync(reply);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>JSON-RPC 1件に答える。通知（id なし）には答えない（null）。</summary>
    internal async Task<string?> HandleAsync(string text, CancellationToken cancellationToken)
    {
        JsonObject request;
        try { request = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return ErrorReply(null, -32700, "Parse error"); }
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        var parameters = request["params"] as JsonObject;
        if (id is null)
            return null;
        try
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26",
                    ["capabilities"] = new JsonObject
                    {
                        ["logging"] = new JsonObject(),
                        ["prompts"] = new JsonObject { ["listChanged"] = true },
                        ["tools"] = new JsonObject { ["listChanged"] = true },
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = "loomo", ["version"] = "1.0.0" },
                },
                "tools/list" => new JsonObject { ["tools"] = ClaudeIdeTools.ToolList() },
                "tools/call" => await _tools.CallAsync(
                    parameters?["name"]?.GetValue<string>() ?? "",
                    parameters?["arguments"] as JsonObject, cancellationToken),
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "ping" => new JsonObject(),
                _ => throw new MethodNotFoundException(method),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(ClaudeIdeTools.JsonOptions);
        }
        catch (MethodNotFoundException)
        {
            return ErrorReply(id, -32601, $"Method not found: {method}");
        }
        catch (OperationCanceledException)
        {
            return null;   // 相手が切れた
        }
    }

    private static string ErrorReply(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString(ClaudeIdeTools.JsonOptions);

    private sealed class MethodNotFoundException(string method) : Exception(method);

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        foreach (var connection in _connections.Keys)
            connection.Dispose();
        _connections.Clear();
        if (_lockPath is not null)
        {
            try { File.Delete(_lockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class Connection(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        public WebSocket Socket { get; } = socket;
        public CancellationToken Closed => _closed.Token;

        public async Task SendAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendGate.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception) { }
            finally { _sendGate.Release(); }
        }

        public void Dispose()
        {
            _closed.Cancel();
            Socket.Dispose();
        }
    }
}
