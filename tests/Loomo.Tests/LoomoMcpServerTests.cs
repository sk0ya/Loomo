using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 部屋を Claude から使う口（§43.6 D）の MCP サーバー。トークンの無い要求・ブラウザからの要求は断る／道具は「置くだけ」の
/// 3 つ／呼び出しはホストへ届き、結果がモデルへの一言で返る／再起動しても同じポートとトークン。
/// 画面の側（フォーカスを奪わない等）は対象外。
/// </summary>
public sealed class LoomoMcpServerTests : IDisposable
{
    private readonly string _endpoint = Path.Combine(Path.GetTempPath(), $"loomo-mcp-{Guid.NewGuid():N}.json");
    private readonly FakeHost _host = new();

    public void Dispose()
    {
        try { File.Delete(_endpoint); } catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public async Task 実際の_HTTP_で_初期化_一覧_呼び出しが通る()
    {
        using var server = new LoomoMcpServer(_host, _endpoint);
        server.Start();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);

        var init = await PostAsync(client, server.Url, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } });
        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());

        // 通知には中身を返さない（202）。
        using var note = await client.PostAsync(server.Url, Json(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);

        var list = await PostAsync(client, server.Url, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        Assert.Equal(["show_file", "show_url", "show_changes", "face_guide", "show_face", "what_human_sees", "motion_guide"],
            list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));

        var call = await PostAsync(client, server.Url, new
        {
            jsonrpc = "2.0", id = "c1", method = "tools/call",
            @params = new { name = "show_file", arguments = new { path = @"src\a.cs", line = 12 } },
        });
        Assert.Equal("c1", call.GetProperty("id").GetString());
        Assert.False(call.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("shown src\\a.cs:12", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal([("file", @"src\a.cs", 12)], _host.Calls);
    }

    [Fact]
    public async Task トークンの無い要求_ブラウザからの要求_POST_以外は断る()
    {
        using var server = new LoomoMcpServer(_host, _endpoint);
        server.Start();
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var good = new Dictionary<string, string> { ["authorization"] = $"Bearer {server.Token}" };

        Assert.Equal(401, (await server.HandleAsync(new("POST", "/mcp", new Dictionary<string, string>(), body))).Status);
        Assert.Equal(401, (await server.HandleAsync(new("POST", "/mcp", new Dictionary<string, string> { ["authorization"] = "Bearer wrong" }, body))).Status);
        Assert.Equal(403, (await server.HandleAsync(new("POST", "/mcp",
            new Dictionary<string, string>(good) { ["origin"] = "http://evil.example" }, body))).Status);
        Assert.Equal(405, (await server.HandleAsync(new("GET", "/mcp", good, ""))).Status);
        Assert.Equal(404, (await server.HandleAsync(new("POST", "/other", good, body))).Status);
        Assert.Equal(400, (await server.HandleAsync(new("POST", "/mcp", good, "{not json"))).Status);
        Assert.Equal(200, (await server.HandleAsync(new("POST", "/mcp", good, body))).Status);
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task 道具の引数が足りない_知らない道具_ホストの失敗は_エラーとして返る()
    {
        var tools = new LoomoMcpTools(_host);

        Assert.False((await tools.CallAsync("show_file", null)).Ok);
        Assert.False((await tools.CallAsync("show_url", [])).Ok);
        Assert.False((await tools.CallAsync("type_into_terminal", [])).Ok);   // 置く以外の道具は無い
        _host.Fail = true;
        var failed = await tools.CallAsync("show_changes", null);
        Assert.False(failed.Ok);
        Assert.Contains("Loomo could not do that", failed.Message);
    }

    [Fact]
    public void 再起動しても_同じポートとトークンで待つ()
    {
        int port;
        string token;
        using (var first = new LoomoMcpServer(_host, _endpoint))
        {
            first.Start();
            (port, token) = (first.Port, first.Token);
        }

        using var second = new LoomoMcpServer(_host, _endpoint);
        second.Start();
        Assert.Equal(token, second.Token);
        Assert.Equal(port, second.Port);

        // 2 つ目の Loomo（ポートは取られている）は別のポートへ移る。
        using var third = new LoomoMcpServer(_host, Path.ChangeExtension(_endpoint, ".copy.json"));
        File.Copy(_endpoint, Path.ChangeExtension(_endpoint, ".copy.json"), overwrite: true);
        third.Start();
        Assert.NotEqual(port, third.Port);
        File.Delete(Path.ChangeExtension(_endpoint, ".copy.json"));
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object message)
    {
        using var response = await client.PostAsync(url, Json(message));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private sealed class FakeHost : ILoomoRoomHost
    {
        public List<(string Kind, string Target, int Line)> Calls { get; } = [];
        public bool Fail { get; set; }

        public Task<LoomoRoomResult> ShowFileAsync(string path, int line)
        {
            Calls.Add(("file", path, line));
            return Task.FromResult(new LoomoRoomResult(true, $"shown {path}:{line}"));
        }

        public Task<LoomoRoomResult> ShowUrlAsync(string url)
        {
            Calls.Add(("url", url, 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }

        public string FaceGuide() => "guide";

        public string MotionGuide() => "motion";

        public Task<LoomoRoomResult> ShowFaceAsync(string id)
        {
            Calls.Add(("face", id, 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }

        public Task<LoomoRoomResult> ShowChangesAsync()
        {
            if (Fail)
                throw new IOException("記録を読めません");
            Calls.Add(("changes", "", 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }
    }
}
