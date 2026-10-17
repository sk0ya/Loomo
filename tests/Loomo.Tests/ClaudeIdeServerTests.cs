using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>Claude Code の IDE 連携サーバー（§38）。本物の WebSocket で繋いで、Claude Code が送る形で叩く。</summary>
public sealed class ClaudeIdeServerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loomo-ide-").FullName;
    private readonly FakeHost _host = new();
    private readonly ClaudeIdeServer _server;

    public ClaudeIdeServerTests()
    {
        _server = new ClaudeIdeServer(_host, () => [@"C:\work\app"], _dir, Path.Combine(_dir, "port"));
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void lockファイルにポートとトークンと作業フォルダーを書き_終了で消す()
    {
        var path = Path.Combine(_dir, "ide", $"{_server.Port}.lock");
        var lockFile = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(_server.AuthToken, (string?)lockFile["authToken"]);
        Assert.Equal("ws", (string?)lockFile["transport"]);
        Assert.Equal(@"C:\work\app", (string?)lockFile["workspaceFolders"]![0]);
        Assert.Equal(_server.Port.ToString(), _server.TerminalEnvironment["CLAUDE_CODE_SSE_PORT"]);

        _server.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task トークンが違えば繋がせない()
    {
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("x-claude-code-ide-authorization", "wrong");
        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}"), CancellationToken.None));
    }

    [Fact]
    public async Task 初期化してツールを並べる()
    {
        using var client = await ConnectAsync();
        var init = await RequestAsync(client, 1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
        Assert.Equal("2025-06-18", (string?)init["result"]!["protocolVersion"]);
        var tools = await RequestAsync(client, 2, "tools/list", null);
        var names = tools["result"]!["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToArray();
        Assert.Contains("openDiff", names);
        Assert.Contains("getDiagnostics", names);
        var unknown = await RequestAsync(client, 3, "no/such", null);
        Assert.Equal(-32601, (int)unknown["error"]!["code"]!);
    }

    [Fact]
    public async Task 診断はURIで絞りJSONの文字列で返す()
    {
        using var client = await ConnectAsync();
        var reply = await CallAsync(client, 1, "getDiagnostics", new JsonObject { ["uri"] = "file:///c%3A/work/app/A.cs" });
        Assert.Equal(@"C:\work\app\A.cs", _host.LastDiagnosticsFilter);
        var files = JsonNode.Parse(Text(reply, 0))!.AsArray();
        Assert.Equal("file:///C:/work/app/A.cs", (string?)files[0]!["uri"]);
        Assert.Equal("Error", (string?)files[0]!["diagnostics"]![0]!["severity"]);
    }

    [Fact]
    public async Task 差分は人が決めるまで待ち_受け入れたら最終本文を返す()
    {
        using var client = await ConnectAsync();
        var pending = CallAsync(client, 7, "openDiff", DiffArgs("t1"));
        var request = await _host.Diffs.Reader.ReadAsync();
        Assert.Equal(@"C:\work\app\A.cs", request.Path);
        Assert.False(pending.IsCompleted);
        request.Decision.SetResult(new ClaudeIdeDiffDecision(true, "edited by human"));
        var reply = await pending;
        Assert.Equal("FILE_SAVED", Text(reply, 0));
        Assert.Equal("edited by human", Text(reply, 1));
    }

    [Fact]
    public async Task Claudeがタブを閉じたら差分は却下で終わる()
    {
        using var client = await ConnectAsync();
        var pending = CallAsync(client, 7, "openDiff", DiffArgs("t2"));
        var request = await _host.Diffs.Reader.ReadAsync();
        var closed = await CallAsync(client, 8, "close_tab", new JsonObject { ["tab_name"] = "t2" });
        Assert.Equal("TAB_CLOSED", Text(closed, 0));
        var reply = await pending;
        Assert.Equal("DIFF_REJECTED", Text(reply, 0));
        Assert.True(request.Token.IsCancellationRequested);   // ホストは表示を片付ける
    }

    [Fact]
    public async Task 選択の変化を通知する()
    {
        using var client = await ConnectAsync();
        await RequestAsync(client, 1, "initialize", null);
        _server.PublishSelection(new ClaudeIdeSelection(@"C:\work\app\A.cs", "abc", 2, 1, 2, 4));
        var note = await ReceiveAsync(client);
        Assert.Equal("selection_changed", (string?)note["method"]);
        Assert.Equal("abc", (string?)note["params"]!["text"]);
        Assert.False((bool)note["params"]!["selection"]!["isEmpty"]!);
        var latest = await CallAsync(client, 2, "getLatestSelection", null);
        Assert.Equal(@"C:\work\app\A.cs", (string?)JsonNode.Parse(Text(latest, 0))!["filePath"]);
    }

    private static JsonObject DiffArgs(string tab) => new()
    {
        ["old_file_path"] = @"C:\work\app\A.cs",
        ["new_file_path"] = @"C:\work\app\A.cs",
        ["new_file_contents"] = "new",
        ["tab_name"] = tab,
    };

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("x-claude-code-ide-authorization", _server.AuthToken);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}"), CancellationToken.None);
        return client;
    }

    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly Dictionary<int, JsonNode> _early = new();

    private async Task<JsonNode> CallAsync(ClientWebSocket client, int id, string tool, JsonObject? args)
        => (await RequestAsync(client, id, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] }))["result"]!;

    private async Task<JsonNode> RequestAsync(ClientWebSocket client, int id, string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
        await client.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        while (true)
        {
            await _receiveGate.WaitAsync();
            try
            {
                if (_early.Remove(id, out var early))
                    return early;
                var reply = await ReceiveAsync(client);
                if (reply["id"] is { } replyId && (int)replyId == id)
                    return reply;
                if (reply["id"] is { } other)
                    _early[(int)other] = reply;
            }
            finally { _receiveGate.Release(); }
        }
    }

    private static async Task<JsonNode> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await client.ReceiveAsync(buffer, cts.Token);
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;
    }

    private static string Text(JsonNode result, int index) => (string)result["content"]![index]!["text"]!;

    private sealed class FakeHost : IClaudeIdeHost
    {
        public System.Threading.Channels.Channel<(string Path, TaskCompletionSource<ClaudeIdeDiffDecision> Decision, CancellationToken Token)> Diffs { get; }
            = System.Threading.Channels.Channel.CreateUnbounded<(string, TaskCompletionSource<ClaudeIdeDiffDecision>, CancellationToken)>();
        public string? LastDiagnosticsFilter { get; private set; }

        public Task<ClaudeIdeSelection?> GetCurrentSelectionAsync() => Task.FromResult<ClaudeIdeSelection?>(null);
        public Task<IReadOnlyList<ClaudeIdeEditor>> GetOpenEditorsAsync() => Task.FromResult<IReadOnlyList<ClaudeIdeEditor>>([]);
        public Task<IReadOnlyList<ClaudeIdeFileDiagnostics>> GetDiagnosticsAsync(string? filePath)
        {
            LastDiagnosticsFilter = filePath;
            return Task.FromResult<IReadOnlyList<ClaudeIdeFileDiagnostics>>(
            [
                new(@"C:\work\app\A.cs", [new("boom", ClaudeIdeSeverity.Error, 1, 2, 1, 5, "CS", "CS0001")]),
            ]);
        }
        public Task OpenFileAsync(string filePath, string? startText, string? endText) => Task.CompletedTask;
        public Task<bool?> IsDirtyAsync(string filePath) => Task.FromResult<bool?>(null);
        public Task<bool> SaveAsync(string filePath) => Task.FromResult(false);

        public async Task<ClaudeIdeDiffDecision> ShowProposedEditAsync(
            string tabName, string filePath, string newContents, CancellationToken cancellationToken)
        {
            var decision = new TaskCompletionSource<ClaudeIdeDiffDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Diffs.Writer.WriteAsync((filePath, decision, cancellationToken));
            await using var _ = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
            return await decision.Task;
        }
    }
}
