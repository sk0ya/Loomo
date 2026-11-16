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
    private readonly BridgeContext _context;
    private readonly LoomoLink _link;

    public StdioBridge(string endpointFile, bool insideLoomo)
        : this(endpointFile, BridgeContext.Unidentified(insideLoomo)) { }

    public StdioBridge(string endpointFile, BridgeContext context)
    {
        _context = context;
        // 待つのはサーバーの打ち切り（30 秒）より少し長く。
        _link = new LoomoLink(endpointFile, context, TimeSpan.FromSeconds(35));
    }

    /// <summary>Loomo が待っている場所（<c>LoomoMcpServer</c> が書く。Loomo ごとの <c>{"pid","port","token"}</c> の一覧）。</summary>
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

        if (!_context.InsideLoomo)
            return AnswerOutsideLoomo(id, method, parameters);
        var response = await _link.PostAsync("/mcp", line, cancel).ConfigureAwait(false);
        if (response is null)
            return Unavailable(id, method);
        // 中身があればそのまま返す（JSON-RPC のエラーも Loomo の言葉のまま）。通知への 202 は中身なし。
        if (response.Body.Length > 0)
            return response.Body;
        return response.Status is >= 200 and < 300 ? null : Unavailable(id, method);
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

    public void Dispose() => _link.Dispose();
}
