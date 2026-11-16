using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.McpBridge;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 接続の土台（§45.3 P0a〜P0c）：橋が名乗った居場所（タブ・祖先の PID）で、そのシェルを持つ Loomo だけが答える／
/// Loomo ごとの待ち場所の一覧／フックが Loomo の答えを標準出力へ返す／フックの設定を前の版のスクリプトから exe へ移す。
/// </summary>
public sealed class ClaudeRoomLinkTests : IDisposable
{
    private const string ListTools = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"loomo-room-link-{Guid.NewGuid():N}");
    private readonly string _endpoint;

    public ClaudeRoomLinkTests()
    {
        Directory.CreateDirectory(_directory);
        _endpoint = Path.Combine(_directory, "mcp-endpoint.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public void 待ち場所は_Loomo_ごとに並び_閉じたら外れ_居ない_Loomo_の項目は次に書くとき消える()
    {
        var alive = new HashSet<int> { 1001, 1002, 1003 };
        using var a = Server(new ShellHost(), 1001, alive);
        using var b = Server(new ShellHost(), 1002, alive);
        a.Start();
        b.Start();

        var file = ReadFile();
        Assert.Equal([1001, 1002], Pids(file));
        Assert.Equal(b.Port, file["port"]!.GetValue<int>());   // 上の段は最後に起動したもの（前の版の橋はここだけ読む）
        Assert.Equal([1002, 1001], LoomoEndpoints.Read(_endpoint, live: null).Select(e => e.Pid));   // 橋は新しい順に尋ねる

        b.Dispose();
        file = ReadFile();
        Assert.Equal([1001], Pids(file));
        Assert.Equal(a.Port, file["port"]!.GetValue<int>());

        // 1001 が落ちた（項目だけ残っている）。次に起動した Loomo が書くときに外す。橋も居ない Loomo の項目は尋ねない。
        alive.Remove(1001);
        Assert.Empty(LoomoEndpoints.Read(_endpoint, live: alive));
        using var c = Server(new ShellHost(), 1003, alive);
        c.Start();
        Assert.Equal([1003], Pids(ReadFile()));
    }

    [Fact]
    public void 前の版の待ち場所のファイルも読める()
    {
        File.WriteAllText(_endpoint, """{"port":50637,"token":"b3d3afeae466ea2a9c146bbeb5f59706"}""");
        var endpoint = Assert.Single(LoomoEndpoints.Read(_endpoint, live: new HashSet<int>()));
        Assert.Equal(50637, endpoint.Port);
    }

    [Fact]
    public async Task 橋は名乗った祖先の中に自分のシェルを持つ_Loomo_にだけ届く()
    {
        var alive = new HashSet<int> { 1001, 1002 };
        var hostA = new ShellHost { Shells = { [111] = Guid.NewGuid() } };
        var tabB = Guid.NewGuid();
        var hostB = new ShellHost { Shells = { [222] = tabB } };
        using var a = Server(hostA, 1001, alive);
        using var b = Server(hostB, 1002, alive);
        a.Start();
        b.Start();

        // claude → シェル 111（A のタブ）→ 常駐ホスト。B が後から起動して先に尋ねられても、B は 421 で断る。
        using var bridgeA = new StdioBridge(_endpoint, Context(ancestors: [40, 111, 9]));
        await bridgeA.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"show_changes"}}""", CancellationToken.None);
        Assert.Equal(1, hostA.Shown);
        Assert.Equal(0, hostB.Shown);

        // 環境変数のタブが無くても（常駐で繋ぎ直したシェル）、祖先の PID で B のタブと分かる。
        using var bridgeB = new StdioBridge(_endpoint, Context(ancestors: [41, 222, 9]));
        await bridgeB.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"show_changes"}}""", CancellationToken.None);
        Assert.Equal(1, hostB.Shown);
        Assert.Equal([tabB], hostB.Callers.Select(c => c.Tab));

        // どちらのタブでもない（Loomo が裏で起こした claude・閉じた Loomo のタブ）：どの Loomo も断る＝外と同じく道具は無い。
        using var stranger = new StdioBridge(_endpoint, Context(ancestors: [42, 333, 9]));
        var reply = JsonDocument.Parse((await stranger.HandleAsync(ListTools, CancellationToken.None))!).RootElement;
        Assert.Equal(0, reply.GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.Null(await new HookRelay(_endpoint, Context(ancestors: [42, 333, 9]), TimeSpan.FromSeconds(3))
            .RelayAsync("""{"hook_event_name":"Notification"}""", CancellationToken.None));
        Assert.Empty(hostA.Hooks);   // 断った客のフックはどちらにも届いていない
        Assert.Empty(hostB.Hooks);
    }

    [Fact]
    public async Task 名乗らない橋は_前の版と同じく受け付ける_名乗って違えば_421()
    {
        using var server = Server(new ShellHost { Shells = { [111] = Guid.NewGuid() } }, 1001, new HashSet<int> { 1001 });
        server.Start();

        var plain = Request("/mcp", ListTools, server.Token);
        Assert.Equal(200, (await server.HandleAsync(plain)).Status);

        var stranger = Request("/mcp", ListTools, server.Token, ("x-loomo-pids", "5,6,7"));
        Assert.Equal(421, (await server.HandleAsync(stranger)).Status);

        var unauthorized = Request("/hook", "{}", "wrong-token-0000000000", ("x-loomo-pids", "111"));
        Assert.Equal(401, (await server.HandleAsync(unauthorized)).Status);
    }

    [Fact]
    public async Task フックは_Loomo_の答えをそのまま返し_タブを渡す()
    {
        var tab = Guid.NewGuid();
        var host = new ShellHost
        {
            Shells = { [111] = tab },
            HookOutput = new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["additionalContext"] = "指摘 2 件" } },
        };
        using var server = Server(host, 1001, new HashSet<int> { 1001 });
        server.Start();

        var relay = new HookRelay(_endpoint, Context(ancestors: [40, 111]), TimeSpan.FromSeconds(3));
        var output = await relay.RelayAsync("""{"hook_event_name":"UserPromptSubmit","prompt":"直して"}""", CancellationToken.None);

        Assert.Equal("指摘 2 件", JsonNode.Parse(output!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>());
        var (caller, hookEvent) = Assert.Single(host.Hooks);
        Assert.Equal(tab, caller.Tab);
        Assert.Equal("UserPromptSubmit", (string?)hookEvent["hook_event_name"]);
    }

    [Fact]
    public async Task フックは_Loomo_の外では何もせず_Loomo_が居なくても黙って終わる()
    {
        var host = new ShellHost { Shells = { [111] = Guid.NewGuid() } };
        using var server = Server(host, 1001, new HashSet<int> { 1001 });
        server.Start();

        var outside = new HookRelay(_endpoint, BridgeContext.Unidentified(insideLoomo: false), TimeSpan.FromSeconds(3));
        Assert.Null(await outside.RelayAsync("""{"hook_event_name":"Notification"}""", CancellationToken.None));
        Assert.Empty(host.Hooks);

        server.Dispose();
        var gone = new HookRelay(_endpoint, Context(ancestors: [40, 111]), TimeSpan.FromSeconds(3));
        var started = DateTime.UtcNow;
        Assert.Null(await gone.RelayAsync("""{"hook_event_name":"Notification"}""", CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));   // 閉じた項目は外れているので、閉じたポートを叩いて待たない
        Assert.Null(await gone.RelayAsync("not json", CancellationToken.None));
    }

    [Fact]
    public void フックを入れ直すと_前の版のスクリプトの項目は外れ_exe_の項目になる()
    {
        var config = Path.Combine(_directory, "claude");
        Directory.CreateDirectory(config);
        var exe = Path.Combine(_directory, "mcp-bridge", "sk0ya.Loomo.McpBridge.exe");
        var legacy = JsonNode.Parse("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"x"}]}]}}""")!.AsObject();
        ClaudeHookInstaller.AddTo(legacy, ClaudeHookInstaller.DefaultScriptPath());
        File.WriteAllText(Path.Combine(config, "settings.json"), legacy.ToJsonString());
        Assert.True(ClaudeHookInstaller.IsLegacyInstalled(config));

        Assert.True(ClaudeHookInstaller.Install(config, exe));

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "settings.json")))!.AsObject();
        var commands = ClaudeHookInstaller.Events
            .SelectMany(name => settings["hooks"]![name]!.AsArray())
            .SelectMany(entry => entry!["hooks"]!.AsArray()).Select(hook => (string)hook!["command"]!).ToList();
        Assert.All(commands, command => Assert.Equal($"\"{exe}\" hook", command));
        Assert.Equal(ClaudeHookInstaller.Events.Length, commands.Count);
        Assert.False(ClaudeHookInstaller.IsLegacyInstalled(config));
        Assert.True(ClaudeHookInstaller.IsInstalled(config, exe));
        Assert.NotNull(settings["hooks"]!["Stop"]);   // 人のフックは残る
        Assert.False(ClaudeHookInstaller.Install(config, exe));

        Assert.True(ClaudeHookInstaller.Uninstall(config, exe));
        Assert.False(ClaudeHookInstaller.IsInstalled(config, exe));
    }

    [Fact]
    public void プロセスの祖先をたどれる()
    {
        var (ancestors, _, live) = ProcessAncestry.Snapshot();
        Assert.NotEmpty(ancestors);
        Assert.DoesNotContain(Environment.ProcessId, ancestors);
        Assert.NotNull(live);
    }

    private LoomoMcpServer Server(ShellHost host, int pid, ISet<int> alive)
        => new(host, _endpoint, pid, alive.Contains);

    private static BridgeContext Context(IReadOnlyList<int> ancestors)
        => new(true, ancestors, null, static () => null);

    private JsonObject ReadFile() => JsonNode.Parse(File.ReadAllText(_endpoint))!.AsObject();

    private static int[] Pids(JsonObject file)
        => file["instances"]!.AsArray().Select(item => item!["pid"]!.GetValue<int>()).ToArray();

    private static LoomoMcpServer.HttpRequest Request(string path, string body, string token, params (string Name, string Value)[] extra)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorization"] = $"Bearer {token}" };
        foreach (var (name, value) in extra)
            headers[name] = value;
        return new LoomoMcpServer.HttpRequest("POST", path, headers, body);
    }

    /// <summary>タブのシェルの PID を持つ Loomo のふり。</summary>
    private sealed class ShellHost : ILoomoRoomHost
    {
        public Dictionary<int, Guid> Shells { get; } = [];
        public JsonObject? HookOutput { get; init; }
        public int Shown { get; private set; }
        public List<LoomoCaller> Callers { get; } = [];
        public List<(LoomoCaller Caller, JsonObject Event)> Hooks { get; } = [];

        public LoomoCaller? Identify(Guid? tab, IReadOnlyList<int> ancestors)
        {
            foreach (var pid in ancestors)
                if (Shells.TryGetValue(pid, out var id))
                {
                    Callers.Add(new LoomoCaller(id));
                    return new LoomoCaller(id);
                }
            return null;
        }

        public Task<JsonObject?> HookAsync(LoomoCaller caller, JsonObject hookEvent)
        {
            Hooks.Add((caller, hookEvent));
            return Task.FromResult(HookOutput?.DeepClone().AsObject());
        }

        public Task<LoomoRoomResult> ShowChangesAsync()
        {
            Shown++;
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }

        public string FaceGuide() => "guide";
        public string MotionGuide() => "motion";
        public Task<LoomoRoomResult> ShowFaceAsync(string id) => Task.FromResult(new LoomoRoomResult(true, "shown"));
        public Task<LoomoRoomResult> ShowFileAsync(string path, int line) => Task.FromResult(new LoomoRoomResult(true, "shown"));
        public Task<LoomoRoomResult> ShowUrlAsync(string url) => Task.FromResult(new LoomoRoomResult(true, "shown"));
    }
}
