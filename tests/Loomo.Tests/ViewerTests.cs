using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.ClaudeCode;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 生えるViewer（§44.2）。定義の読み方と許しの指紋／Viewerのオリジン／置き場（Viewerのフォルダーの外へ出ない）／PC が覚えること
/// （許し・開いているViewer・枯れ方）／部屋の口の線引き（挙げていない口は断る・ワークスペースの外は読めない・書いていない
/// コマンドは回せない・行は固定で引数は環境変数に入る）。画面（WebView2・許しのカード）は対象外。
/// </summary>
public sealed class ViewerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-viewers-{Guid.NewGuid():N}");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"loomo-viewers-ws-{Guid.NewGuid():N}");
    private readonly ViewerStore _store;
    private readonly FakeRoom _room = new();

    public ViewerTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_workspace);
        _store = new ViewerStore(_root);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _workspace })
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    private ViewerManifest Place(string id, string json, string html = "<p>viewer</p>")
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "viewer.json"), json);
        File.WriteAllText(Path.Combine(folder, "index.html"), html);
        var entry = _store.Find(id);
        Assert.True(entry.Manifest is not null, entry.Error);
        return entry.Manifest!;
    }

    private ViewerRoomApi Api() => new(_store, new FakeWorkspaceService(_workspace), _room);

    private static Task<JsonNode?> Call(ViewerRoomApi api, ViewerManifest viewer, string method, JsonObject? args = null,
        Action<string, JsonNode>? emit = null)
        => api.CallAsync(viewer, method, args, emit ?? ((_, _) => { }), CancellationToken.None);

    // ── 定義 ─────────────────────────────────────────────────

    [Fact]
    public void 定義を読む_題が無ければ名前_コマンドは文字列でもオブジェクトでも()
    {
        var viewer = ViewerManifest.Parse("ports", """
            {
              // コメントと末尾のカンマは許す
              "description": "握っているポート",
              "api": ["run", "open", "run"],
              "commands": {
                "list": "netstat -ano",
                "kill": { "command": "Stop-Process -Id $env:LOOMO_ARG_1", "cwd": "viewer", "timeoutSeconds": 5000 },
              },
            }
            """, out var error);

        Assert.Null(error);
        Assert.Equal("ports", viewer!.Title);
        Assert.Equal(["run", "open"], viewer.Api);
        Assert.Equal("netstat -ano", viewer.Command("list")!.Command);
        Assert.Equal(ViewerCommand.CwdWorkspace, viewer.Command("list")!.Cwd);
        Assert.Equal(ViewerCommand.CwdViewer, viewer.Command("kill")!.Cwd);
        Assert.Equal(ViewerCommand.MaxTimeoutSeconds, viewer.Command("kill")!.TimeoutSeconds);
        Assert.Null(viewer.Command("other"));
    }

    [Theory]
    [InlineData("Ports", "{}")]                                               // 大文字の名前
    [InlineData("a/b", "{}")]
    [InlineData("ports", "[]")]
    [InlineData("ports", "{ \"api\": [\"shell\"] }")]                         // 知らない口
    [InlineData("ports", "{ \"api\": [], \"commands\": { \"x\": \"dir\" } }")] // run を挙げずにコマンド
    [InlineData("ports", "{ \"api\": [\"run\"], \"commands\": { \"x\": \"\" } }")]
    [InlineData("ports", "{ \"api\": [\"run\"], \"commands\": { \"x\": { \"command\": \"dir\", \"cwd\": \"C:/\" } } }")]
    [InlineData("ports", "{ not json")]
    public void 読めない定義は_直せる理由つきで断る(string id, string json)
    {
        Assert.Null(ViewerManifest.Parse(id, json, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 指紋は口とコマンドの行で決まり_題や並び順では変わらない()
    {
        var first = ViewerManifest.Parse("a", """{ "title": "A", "api": ["run", "open"], "commands": { "x": "dir", "y": "ls" } }""", out _)!;
        var retitled = ViewerManifest.Parse("a", """{ "title": "B", "description": "d", "api": ["open", "run"], "commands": { "y": "ls", "x": "dir" } }""", out _)!;
        var moreApi = ViewerManifest.Parse("a", """{ "api": ["run", "open", "fs.read"], "commands": { "x": "dir", "y": "ls" } }""", out _)!;
        var otherLine = ViewerManifest.Parse("a", """{ "api": ["run", "open"], "commands": { "x": "dir /s", "y": "ls" } }""", out _)!;

        Assert.Equal(first.Fingerprint, retitled.Fingerprint);
        Assert.NotEqual(first.Fingerprint, moreApi.Fingerprint);
        Assert.NotEqual(first.Fingerprint, otherLine.Fingerprint);
    }

    [Theory]
    [InlineData("""{ }""", false)]
    [InlineData("""{ "api": ["workspace", "open", "fs.read", "git"] }""", false)]   // 読む・置くだけ
    [InlineData("""{ "api": ["open", "run"], "commands": { "x": "dir" } }""", true)] // 任意のコマンド
    [InlineData("""{ "api": ["fs.read", "net"] }""", true)]                         // 読んだものを外へ送れる
    [InlineData("""{ "api": ["motion"] }""", true)]
    [InlineData("""{ "api": ["orders"] }""", true)]
    public void 許しが要るのは_読む口と置く口の外を挙げたViewerだけ(string json, bool needs)
        => Assert.Equal(needs, ViewerManifest.Parse("a", json, out _)!.NeedsApproval);

    [Fact]
    public void ViewerのオリジンはViewerごと_ほかのホストはViewerではない()
    {
        Assert.True(ViewerUrl.TryParse("https://ports.viewer.loomo/", out var id, out var path));
        Assert.Equal(("ports", "index.html"), (id, path));
        Assert.True(ViewerUrl.TryParse("https://ports.viewer.loomo/js/app%20x.js?v=1", out _, out path));
        Assert.Equal("js/app x.js", path);

        Assert.True(ViewerUrl.IsOf("ports", ViewerUrl.Index("ports")));
        Assert.False(ViewerUrl.IsOf("ports", "https://other.viewer.loomo/index.html"));
        Assert.False(ViewerUrl.IsOf("ports", "https://file.loomo/C:/x/index.html"));
        Assert.False(ViewerUrl.IsOf("ports", "http://ports.viewer.loomo/index.html"));       // https だけ
        Assert.False(ViewerUrl.IsOf("ports", "https://ports.viewer.loomo.example.com/"));
    }

    // ── 置き場 ───────────────────────────────────────────────

    [Fact]
    public void 置き場はViewerのフォルダーの中だけを答える()
    {
        Place("ports", "{}");
        Directory.CreateDirectory(Path.Combine(_root, "ports", "js"));
        File.WriteAllText(Path.Combine(_root, "ports", "js", "app.js"), "1");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "x");
        Assert.True(_store.WriteState("ports", "{\"a\":1}"));

        Assert.NotNull(_store.ResolveFile("ports", "index.html"));
        Assert.NotNull(_store.ResolveFile("ports", "js/app.js"));
        Assert.Null(_store.ResolveFile("ports", "../secret.txt"));
        Assert.Null(_store.ResolveFile("ports", "missing.js"));
        Assert.Null(_store.ResolveFile("ports", ViewerStore.StateFileName));   // 置き場はページに配らない
        Assert.Null(_store.ResolveFile("../ports", "index.html"));
    }

    [Fact]
    public void 一覧は_viewer_json_のあるフォルダーだけ_読めないViewerは理由つきで並ぶ()
    {
        Place("b-viewer", """{ "title": "B" }""");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-viewer"));
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", "viewer.json"), "{ oops");
        Directory.CreateDirectory(Path.Combine(_root, "no-page"));
        File.WriteAllText(Path.Combine(_root, "no-page", "viewer.json"), "{}");

        var viewers = _store.List();

        Assert.Equal(["b-viewer", "broken", "no-page"], viewers.Select(viewer => viewer.Id));
        Assert.Equal("B", viewers[0].Title);
        Assert.Null(viewers[1].Manifest);
        Assert.Contains("not valid JSON", viewers[1].Error);
        Assert.Contains("index.html", viewers[2].Error);
    }

    [Fact]
    public void 許しは指紋ごと_定義が変わったら聞き直す_開いているViewerと回数は起動をまたぐ()
    {
        var path = Path.Combine(_root, "state.json");
        var viewer = Place("ports", """{ "api": ["open"] }""");
        var state = new ViewerRoomState(path);
        Assert.False(state.IsApproved(viewer));

        state.Approve(viewer);
        state.SetOpenViewers(["ports", "other"], "other");
        state.RecordOpened("ports", new DateTime(2026, 11, 15));
        state.RecordOpened("ports", new DateTime(2026, 11, 15));

        var reopened = new ViewerRoomState(path);
        Assert.True(reopened.IsApproved(viewer));
        Assert.Equal(["ports", "other"], reopened.OpenViewers.Open);
        Assert.Equal("other", reopened.OpenViewers.Active);
        Assert.Equal(new DateTime(2026, 11, 15), reopened.LastOpened("ports"));

        var changed = Place("ports", """{ "api": ["open", "fs.read"] }""");
        Assert.False(reopened.IsApproved(changed));
    }

    [Fact]
    public void 三十日開かなかったViewerは枯れる_開けば戻る()
    {
        var state = new ViewerRoomState(Path.Combine(_root, "state.json"));
        var created = new DateTime(2026, 10, 1);

        Assert.False(state.IsWithered("ports", created, new DateTime(2026, 10, 20)));
        Assert.True(state.IsWithered("ports", created, new DateTime(2026, 11, 1)));    // 一度も開かず 30 日

        state.RecordOpened("ports", new DateTime(2026, 10, 25));
        Assert.False(state.IsWithered("ports", created, new DateTime(2026, 11, 1)));
        Assert.True(state.IsWithered("ports", created, new DateTime(2026, 11, 25)));
    }

    // ── 部屋の口 ─────────────────────────────────────────────

    [Fact]
    public async Task 挙げていない口は断る()
    {
        var viewer = Place("plain", "{}");
        var api = Api();

        foreach (var method in new[] { "workspace.folders", "workspace.activeFile", "open", "openUrl", "fs.read", "fs.list", "git.status", "run" })
            await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, method, new JsonObject { ["path"] = "a.txt" }));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "terminal.type"));
        Assert.Empty(_room.Calls);
    }

    [Fact]
    public async Task 読めるのはワークスペースの中だけ()
    {
        File.WriteAllText(Path.Combine(_workspace, "a.txt"), "中身");
        Directory.CreateDirectory(Path.Combine(_workspace, "sub"));
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "x");
        var viewer = Place("reader", """{ "api": ["fs.read", "open", "workspace"] }""");
        var api = Api();

        Assert.Equal("中身", (string?)await Call(api, viewer, "fs.read", new JsonObject { ["path"] = "a.txt" }));
        Assert.Equal("中身", (string?)await Call(api, viewer, "fs.read", new JsonObject { ["path"] = Path.Combine(_workspace, "a.txt") }));
        var listed = (JsonArray)(await Call(api, viewer, "fs.list", new JsonObject { ["path"] = _workspace }))!;
        Assert.Equal(["a.txt", "sub"], listed.Select(entry => (string)entry!["name"]!).Order());

        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "fs.read", new JsonObject { ["path"] = outside }));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "fs.read", new JsonObject { ["path"] = "..\\" + Path.GetFileName(_root) + "\\outside.txt" }));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "open", new JsonObject { ["path"] = outside }));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "openUrl", new JsonObject { ["url"] = "file:///C:/x" }));

        await Call(api, viewer, "open", new JsonObject { ["path"] = "a.txt", ["line"] = 3 });
        Assert.Equal([("open", Path.Combine(_workspace, "a.txt"), 3)], _room.Calls);
        var folders = (JsonArray)(await Call(api, viewer, "workspace.folders"))!;
        Assert.Equal(_workspace, (string)folders[0]!["path"]!);
    }

    [Fact]
    public async Task 置き場と通知は_口を挙げなくても使える()
    {
        var viewer = Place("notes", "{}");
        var api = Api();

        Assert.Null(await Call(api, viewer, "state.get"));
        await Call(api, viewer, "state.set", new JsonObject { ["value"] = new JsonObject { ["n"] = 2 } });
        Assert.Equal(2, (int)(await Call(api, viewer, "state.get"))!["n"]!);
        await Call(api, viewer, "state.set", new JsonObject { ["value"] = null });
        Assert.Null(await Call(api, viewer, "state.get"));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "state.set", []));

        await Call(api, viewer, "toast", new JsonObject { ["text"] = "できた" });
        Assert.Equal([("toast", "できた", 0)], _room.Calls);
    }

    [Fact]
    public async Task 回せるのは書いてあるコマンドだけ_行は固定で_渡した値は環境変数と変数に入る()
    {
        var viewer = Place("runner", """
            {
              "api": ["run"],
              "commands": {
                "echo": { "command": "Write-Output \"arg=$env:LOOMO_ARG_1 n=$env:LOOMO_ARGC\"; Write-Output \"in=$LoomoInput\"; exit 3", "timeoutSeconds": 30 },
                "where": { "command": "(Get-Location).Path", "cwd": "viewer" }
              }
            }
            """);
        var api = Api();
        var lines = new List<string>();

        var result = await Call(api, viewer, "run", new JsonObject
        {
            ["name"] = "echo",
            ["args"] = new JsonArray("a; Remove-Item x"),   // 行には入らないので、ただの文字として出る
            ["stdin"] = "こんにちは",
            ["run"] = "r1",
        }, (name, data) =>
        {
            Assert.Equal("run.output", name);
            Assert.Equal("r1", (string)data["run"]!);
            lock (lines)
                lines.Add((string)data["line"]!);
        });

        Assert.Equal(3, (int)result!["exitCode"]!);
        Assert.False((bool)result["timedOut"]!);
        Assert.Contains("arg=a; Remove-Item x n=1", lines);
        Assert.Contains("in=こんにちは", lines);
        Assert.Contains("arg=a; Remove-Item x n=1", (string)result["output"]!);

        var where = await Call(api, viewer, "run", new JsonObject { ["name"] = "where" });
        Assert.Contains(Path.Combine(_root, "runner"), (string)where!["output"]!, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "run", new JsonObject { ["name"] = "format-disk" }));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, viewer, "run", new JsonObject { ["name"] = "Write-Output hi" }));
    }

    [Fact]
    public async Task 期限を過ぎたコマンドは止めて_そう言う()
    {
        var viewer = Place("slow", """{ "api": ["run"], "commands": { "sleep": { "command": "Start-Sleep -Seconds 60", "timeoutSeconds": 1 } } }""");

        var result = await Call(Api(), viewer, "run", new JsonObject { ["name"] = "sleep" });

        Assert.True((bool)result!["timedOut"]!);
        Assert.Equal(-1, (int)result["exitCode"]!);
    }

    // ── Claude から ─────────────────────────────────────────

    [Fact]
    public async Task 書き方の道具は_置き場所と口と_いまあるViewerを返す()
    {
        Place("ports", """{ "title": "Ports", "description": "握っているポート" }""");
        _room.Guide = ViewerGuide.Text(_store);
        var tools = new LoomoMcpTools(_room);

        var guide = await tools.CallAsync("viewer_guide", null);
        var shown = await tools.CallAsync("show_viewer", new JsonObject { ["id"] = "ports" });
        var missing = await tools.CallAsync("show_viewer", []);

        Assert.True(guide.Ok);
        Assert.Contains(_root, guide.Message);
        Assert.Contains("loomo.run(", guide.Message);
        Assert.Contains("- ports: Ports — 握っているポート", guide.Message);
        foreach (var name in ViewerApi.All)
            Assert.Contains(name, guide.Message);
        Assert.True(shown.Ok);
        Assert.False(missing.Ok);
        Assert.Equal([("viewer", "ports", 0)], _room.Calls);
    }

    private sealed class FakeRoom : IViewerRoom, ILoomoRoomHost
    {
        public List<(string Kind, string Target, int Line)> Calls { get; } = [];
        public string Guide { get; set; } = "";

        public Task<JsonNode?> ActiveFileAsync() => Task.FromResult<JsonNode?>(null);

        public Task<bool> OpenFileAsync(string path, int line)
        {
            Calls.Add(("open", path, line));
            return Task.FromResult(true);
        }

        public Task<bool> OpenUrlAsync(string url)
        {
            Calls.Add(("url", url, 0));
            return Task.FromResult(true);
        }

        public Task<JsonNode> GitStatusAsync() => Task.FromResult<JsonNode>(new JsonArray());

        public void Toast(string text) => Calls.Add(("toast", text, 0));

        public Task<LoomoRoomResult> ShowFileAsync(string path, int line) => Task.FromResult(new LoomoRoomResult(true, ""));
        public Task<LoomoRoomResult> ShowUrlAsync(string url) => Task.FromResult(new LoomoRoomResult(true, ""));
        public Task<LoomoRoomResult> ShowChangesAsync() => Task.FromResult(new LoomoRoomResult(true, ""));
        public string ViewerGuide() => Guide;
        public string MotionGuide() => "";

        public Task<LoomoRoomResult> ShowViewerAsync(string id)
        {
            Calls.Add(("viewer", id, 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }
    }
}
