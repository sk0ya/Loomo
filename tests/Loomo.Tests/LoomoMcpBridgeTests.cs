using System.IO;
using System.Text.Json;
using sk0ya.Loomo.McpBridge;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Claude Code と Loomo の MCP サーバーをつなぐ橋（§43.6 D）。標準入出力の 1 行を本物のサーバーへ渡して答えを 1 行で返す／
/// 行き先は呼ばれるたびにファイルから読む（Loomo のポートが移っても通じる——環境変数で渡していた頃はここで切れた）／
/// Loomo が動いていなければ読める一言／Loomo のターミナルの外では道具を出さず、Loomo へも問い合わせない。
/// あわせて、橋を bin の外へ写す側（掴まれているファイルは脇へどけて置き直す）。
/// </summary>
public sealed class LoomoMcpBridgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"loomo-mcp-bridge-{Guid.NewGuid():N}");
    private readonly string _endpoint;
    private readonly FakeHost _host = new();

    public LoomoMcpBridgeTests()
    {
        Directory.CreateDirectory(_directory);
        _endpoint = Path.Combine(_directory, "mcp-endpoint.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public async Task 標準入出力の_1_行が_サーバーへ届き_答えが_1_行で返る()
    {
        using var server = new LoomoMcpServer(_host, _endpoint);
        server.Start();
        using var bridge = new StdioBridge(_endpoint, insideLoomo: true);
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":"c1","method":"tools/call","params":{"name":"show_file","arguments":{"path":"docs/設計.md","line":3}}}"""));
        var output = new StringWriter { NewLine = "\n" };

        await bridge.RunAsync(input, output, CancellationToken.None);

        // 通知と空行には何も返さないので 3 行。
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
        Assert.Equal(3, replies.Count);
        Assert.Equal("2025-06-18", replies[0].GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(["show_file", "show_url", "show_changes", "face_guide", "show_face", "what_human_sees", "motion_guide"],
            replies[1].GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        Assert.Equal("c1", replies[2].GetProperty("id").GetString());
        Assert.False(replies[2].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal([("file", "docs/設計.md", 3)], _host.Calls);
    }

    [Fact]
    public async Task 行き先は呼ばれるたびに読むので_Loomo_のポートが移っても通じる()
    {
        using var bridge = new StdioBridge(_endpoint, insideLoomo: true);
        const string list = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        var otherEndpoint = Path.Combine(_directory, "other.json");
        using (var first = new LoomoMcpServer(_host, _endpoint))
        {
            first.Start();
            Assert.True(Parse((await bridge.HandleAsync(list, CancellationToken.None))!).TryGetProperty("result", out _));

            // 別のポート・別のトークンで待つ Loomo に入れ替わる（橋は動いたまま）。
            using var second = new LoomoMcpServer(_host, otherEndpoint);
            second.Start();
            Assert.NotEqual(first.Port, second.Port);
            first.Dispose();
            File.Copy(otherEndpoint, _endpoint, overwrite: true);

            Assert.True(Parse((await bridge.HandleAsync(list, CancellationToken.None))!).TryGetProperty("result", out _));
        }
    }

    [Fact]
    public async Task Loomo_が動いていなければ_道具の呼び出しには読める一言_ほかはエラー()
    {
        // 場所のファイルが無い場合と、書いてあるポートで誰も待っていない場合。
        using var bridge = new StdioBridge(_endpoint, insideLoomo: true);
        for (var round = 0; round < 2; round++)
        {
            var call = Parse((await bridge.HandleAsync(
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"show_changes"}}""", CancellationToken.None))!);
            Assert.Equal(7, call.GetProperty("id").GetInt32());
            Assert.True(call.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("Loomo is not running", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

            var list = Parse((await bridge.HandleAsync("""{"jsonrpc":"2.0","id":8,"method":"tools/list"}""", CancellationToken.None))!);
            Assert.Equal(-32000, list.GetProperty("error").GetProperty("code").GetInt32());

            Assert.Null(await bridge.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None));

            using (var gone = new LoomoMcpServer(_host, _endpoint))
                gone.Start();   // ファイルだけ残して閉じる
        }
        Assert.Equal(-32700, Parse((await bridge.HandleAsync("{not json", CancellationToken.None))!).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Loomo_のターミナルの外では_つながるが道具は無く_Loomo_へも問い合わせない()
    {
        using var server = new LoomoMcpServer(_host, _endpoint);
        server.Start();
        using var bridge = new StdioBridge(_endpoint, insideLoomo: false);

        var init = Parse((await bridge.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""", CancellationToken.None))!);
        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Null(await bridge.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None));
        var list = Parse((await bridge.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);
        Assert.Equal(0, list.GetProperty("result").GetProperty("tools").GetArrayLength());
        var call = Parse((await bridge.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"show_url","arguments":{"url":"https://example.com"}}}""",
            CancellationToken.None))!);
        Assert.True(call.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public void 橋を写す_同じ中身は触らず_掴まれているファイルは脇へどけて置き直す()
    {
        var source = Path.Combine(_directory, "source");
        var target = Path.Combine(_directory, "target");
        Directory.CreateDirectory(source);
        Assert.False(LoomoMcpBridgeInstaller.Install(source, target));   // 元が空なら exe は無い

        var exe = Path.Combine(source, LoomoMcpBridgeInstaller.ExeName);
        File.WriteAllText(exe, "v1");
        File.WriteAllText(Path.Combine(source, "sk0ya.Loomo.McpBridge.dll"), "lib");
        Assert.True(LoomoMcpBridgeInstaller.Install(source, target));
        var installed = Path.Combine(target, LoomoMcpBridgeInstaller.ExeName);
        Assert.Equal("v1", File.ReadAllText(installed));

        // 同じ中身なら書き直さない。
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(installed, stamp);
        Assert.True(LoomoMcpBridgeInstaller.Install(source, target));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(installed));

        // 動いている橋が掴んでいる（上書きはできないが名前は変えられる）あいだに新しい版が来る。
        File.WriteAllText(exe, "v2");
        using (new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.True(LoomoMcpBridgeInstaller.Install(source, target));
            Assert.Equal("v2", File.ReadAllText(installed));
            Assert.Single(Directory.GetFiles(target, "*.old-*"));
        }

        // 脇へどけたものは、掴まれなくなった次の回で消える。
        Assert.True(LoomoMcpBridgeInstaller.Install(source, target));
        Assert.Empty(Directory.GetFiles(target, "*.old-*"));
        Assert.Equal(2, Directory.GetFiles(target).Length);
    }

    [Fact]
    public void 登録のコマンドは_前の定義を外してから_橋の場所だけを登録する()
    {
        var command = LoomoMcpBridgeInstaller.RegistrationCommand(@"C:\Users\o'neil\AppData\Local\Loomo\mcp-bridge\sk0ya.Loomo.McpBridge.exe");

        Assert.StartsWith("claude mcp remove --scope user loomo", command);
        Assert.EndsWith(@"claude mcp add --scope user loomo -- 'C:\Users\o''neil\AppData\Local\Loomo\mcp-bridge\sk0ya.Loomo.McpBridge.exe'", command);
        Assert.DoesNotContain("LOOMO_MCP", command);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class FakeHost : ILoomoRoomHost
    {
        public List<(string Kind, string Target, int Line)> Calls { get; } = [];

        public string FaceGuide() => "guide";

        public string MotionGuide() => "motion";

        public Task<LoomoRoomResult> ShowFaceAsync(string id) => Task.FromResult(new LoomoRoomResult(true, "shown"));

        public Task<LoomoRoomResult> ShowFileAsync(string path, int line)
        {
            Calls.Add(("file", path, line));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }

        public Task<LoomoRoomResult> ShowUrlAsync(string url)
        {
            Calls.Add(("url", url, 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }

        public Task<LoomoRoomResult> ShowChangesAsync()
        {
            Calls.Add(("changes", "", 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }
    }
}
