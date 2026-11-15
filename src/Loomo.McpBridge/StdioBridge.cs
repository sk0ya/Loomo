using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.McpBridge;

/// <summary>
/// 標準入出力の MCP を Loomo の MCP サーバー（HTTP）へ渡す管（§43.6 D）。メッセージの中身は見ない——道具の一覧も呼び出しも
/// Loomo が答える。ここで決めるのは 3 つだけ：
/// <list type="bullet">
/// <item><b>行き先は呼ばれるたびにファイルから読む</b>（ポートとトークン）。Loomo が再起動してポートが移っても、
/// 動き続けている claude からそのまま通じる。</item>
/// <item><b>Loomo のターミナルの外では道具を出さない</b>。定義はユーザーの設定に 1 つなので、どの端末の claude からも
/// 起こされる。外では Loomo へつなぎに行かず、空の一覧を自分で答える（失敗にすると、外の claude が毎回「接続に失敗」と言う）。</item>
/// <item><b>Loomo が動いていないとき</b>は、道具の呼び出しには読める一言を、ほかの要求にはエラーを返す。</item>
/// </list>
/// </summary>
internal sealed class StdioBridge : IDisposable
{
    private readonly string _endpointFile;
    private readonly bool _insideLoomo;
    private readonly HttpClient _http;

    public StdioBridge(string endpointFile, bool insideLoomo)
    {
        _endpointFile = endpointFile;
        _insideLoomo = insideLoomo;
        // 相手は 127.0.0.1 だけ。システムのプロキシは通さない。待つのはサーバーの打ち切り（30 秒）より少し長く。
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(35) };
    }

    /// <summary>Loomo が待っている場所（<c>LoomoMcpServer</c> が書く。形は <c>{"port":…,"token":"…"}</c>）。</summary>
    public static string DefaultEndpointFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "mcp-endpoint.json");

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancel)
    {
        while (await input.ReadLineAsync(cancel).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (await HandleAsync(line, cancel).ConfigureAwait(false) is not { } reply)
                continue;
            // 1 行＝1 メッセージ。JSON の文字列の中の改行はエスケープされているので、生の改行は字間の空白だけ。
            await output.WriteLineAsync(reply.ReplaceLineEndings("")).ConfigureAwait(false);
            await output.FlushAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>メッセージ 1 つに答える。通知（id なし）には何も返さない。</summary>
    internal async Task<string?> HandleAsync(string line, CancellationToken cancel)
    {
        JsonNode? id;
        string method;
        JsonObject? parameters;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject call)
                return Error(null, -32600, "Invalid Request");
            id = call["id"]?.DeepClone();
            method = call["method"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : "";
            parameters = call["params"] as JsonObject;
        }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }

        if (!_insideLoomo)
            return AnswerOutsideLoomo(id, method, parameters);
        if (ReadEndpoint() is not var (port, token))
            return Unavailable(id, method);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
            {
                Content = new StringContent(line, new UTF8Encoding(false), "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            // 中身があればそのまま返す（JSON-RPC のエラーも Loomo の言葉のまま）。通知への 202 は中身なし。
            if (body.Length > 0)
                return body;
            return response.IsSuccessStatusCode ? null : Unavailable(id, method);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            return Unavailable(id, method);
        }
    }

    /// <summary>Loomo の外：つながるが道具は無い。Loomo へは問い合わせない。</summary>
    private static string? AnswerOutsideLoomo(JsonNode? id, string method, JsonObject? parameters)
    {
        if (id is null)
            return null;
        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = parameters?["protocolVersion"] is JsonValue version && version.TryGetValue<string>(out var text)
                    ? text : "2025-03-26",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "Loomo", ["version"] = "1" },
            }),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray() }),
            "tools/call" => ToolError(id, "This session is not running in a Loomo terminal, so there is nothing to show."),
            _ => Error(id, -32601, "Method not found"),
        };
    }

    private static string? Unavailable(JsonNode? id, string method)
    {
        const string message = "Loomo is not running (or its Claude Code integration is turned off), so nothing was shown.";
        if (id is null)
            return null;
        // 道具の呼び出しは「結果がエラー」で返す（モデルが一言を読める）。それ以外はプロトコルのエラー。
        return method == "tools/call" ? ToolError(id, message) : Error(id, -32000, message);
    }

    /// <summary>ポートとトークン。読めない・形が違うなら無し（確かめ方は書く側の <c>LoomoMcpServer</c> と同じ）。</summary>
    private (int Port, string Token)? ReadEndpoint()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_endpointFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("port", out var p) || !p.TryGetInt32(out var port) || port is <= 0 or >= 65536
                || !doc.RootElement.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String
                || t.GetString() is not { Length: >= 16 } token || !token.All(char.IsAsciiLetterOrDigit))
                return null;
            return (port, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string ToolError(JsonNode id, string message) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    });

    private static string Result(JsonNode id, JsonObject result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();

    public void Dispose() => _http.Dispose();
}
