using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Faces;
using sk0ya.Loomo.Services.ClaudeCode;
using sk0ya.Loomo.Services.Faces;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 生える面（§44.2）。定義の読み方と許しの指紋／面のオリジン／置き場（面のフォルダーの外へ出ない）／PC が覚えること
/// （許し・開いている面・枯れ方）／部屋の口の線引き（挙げていない口は断る・ワークスペースの外は読めない・書いていない
/// コマンドは回せない・行は固定で引数は環境変数に入る）。画面（WebView2・許しのカード）は対象外。
/// </summary>
public sealed class FaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-faces-{Guid.NewGuid():N}");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"loomo-faces-ws-{Guid.NewGuid():N}");
    private readonly FaceStore _store;
    private readonly FakeRoom _room = new();

    public FaceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_workspace);
        _store = new FaceStore(_root);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _workspace })
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    private FaceManifest Place(string id, string json, string html = "<p>face</p>")
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "face.json"), json);
        File.WriteAllText(Path.Combine(folder, "index.html"), html);
        var entry = _store.Find(id);
        Assert.True(entry.Manifest is not null, entry.Error);
        return entry.Manifest!;
    }

    private FaceRoomApi Api() => new(_store, new FakeWorkspaceService(_workspace), _room);

    private static Task<JsonNode?> Call(FaceRoomApi api, FaceManifest face, string method, JsonObject? args = null,
        Action<string, JsonNode>? emit = null)
        => api.CallAsync(face, method, args, emit ?? ((_, _) => { }), CancellationToken.None);

    // ── 定義 ─────────────────────────────────────────────────

    [Fact]
    public void 定義を読む_題が無ければ名前_コマンドは文字列でもオブジェクトでも()
    {
        var face = FaceManifest.Parse("ports", """
            {
              // コメントと末尾のカンマは許す
              "description": "握っているポート",
              "api": ["run", "open", "run"],
              "commands": {
                "list": "netstat -ano",
                "kill": { "command": "Stop-Process -Id $env:LOOMO_ARG_1", "cwd": "face", "timeoutSeconds": 5000 },
              },
            }
            """, out var error);

        Assert.Null(error);
        Assert.Equal("ports", face!.Title);
        Assert.Equal(["run", "open"], face.Api);
        Assert.Equal("netstat -ano", face.Command("list")!.Command);
        Assert.Equal(FaceCommand.CwdWorkspace, face.Command("list")!.Cwd);
        Assert.Equal(FaceCommand.CwdFace, face.Command("kill")!.Cwd);
        Assert.Equal(FaceCommand.MaxTimeoutSeconds, face.Command("kill")!.TimeoutSeconds);
        Assert.Null(face.Command("other"));
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
        Assert.Null(FaceManifest.Parse(id, json, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 指紋は口とコマンドの行で決まり_題や並び順では変わらない()
    {
        var first = FaceManifest.Parse("a", """{ "title": "A", "api": ["run", "open"], "commands": { "x": "dir", "y": "ls" } }""", out _)!;
        var retitled = FaceManifest.Parse("a", """{ "title": "B", "description": "d", "api": ["open", "run"], "commands": { "y": "ls", "x": "dir" } }""", out _)!;
        var moreApi = FaceManifest.Parse("a", """{ "api": ["run", "open", "fs.read"], "commands": { "x": "dir", "y": "ls" } }""", out _)!;
        var otherLine = FaceManifest.Parse("a", """{ "api": ["run", "open"], "commands": { "x": "dir /s", "y": "ls" } }""", out _)!;

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
    public void 許しが要るのは_読む口と置く口の外を挙げた面だけ(string json, bool needs)
        => Assert.Equal(needs, FaceManifest.Parse("a", json, out _)!.NeedsApproval);

    [Fact]
    public void 面のオリジンは面ごと_ほかのホストは面ではない()
    {
        Assert.True(FaceUrl.TryParse("https://ports.face.loomo/", out var id, out var path));
        Assert.Equal(("ports", "index.html"), (id, path));
        Assert.True(FaceUrl.TryParse("https://ports.face.loomo/js/app%20x.js?v=1", out _, out path));
        Assert.Equal("js/app x.js", path);

        Assert.True(FaceUrl.IsOf("ports", FaceUrl.Index("ports")));
        Assert.False(FaceUrl.IsOf("ports", "https://other.face.loomo/index.html"));
        Assert.False(FaceUrl.IsOf("ports", "https://file.loomo/C:/x/index.html"));
        Assert.False(FaceUrl.IsOf("ports", "http://ports.face.loomo/index.html"));       // https だけ
        Assert.False(FaceUrl.IsOf("ports", "https://ports.face.loomo.example.com/"));
    }

    // ── 置き場 ───────────────────────────────────────────────

    [Fact]
    public void 置き場は面のフォルダーの中だけを答える()
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
        Assert.Null(_store.ResolveFile("ports", FaceStore.StateFileName));   // 置き場はページに配らない
        Assert.Null(_store.ResolveFile("../ports", "index.html"));
    }

    [Fact]
    public void 一覧は_face_json_のあるフォルダーだけ_読めない面は理由つきで並ぶ()
    {
        Place("b-face", """{ "title": "B" }""");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-face"));
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", "face.json"), "{ oops");
        Directory.CreateDirectory(Path.Combine(_root, "no-page"));
        File.WriteAllText(Path.Combine(_root, "no-page", "face.json"), "{}");

        var faces = _store.List();

        Assert.Equal(["b-face", "broken", "no-page"], faces.Select(face => face.Id));
        Assert.Equal("B", faces[0].Title);
        Assert.Null(faces[1].Manifest);
        Assert.Contains("not valid JSON", faces[1].Error);
        Assert.Contains("index.html", faces[2].Error);
    }

    [Fact]
    public void 許しは指紋ごと_定義が変わったら聞き直す_開いている面と回数は起動をまたぐ()
    {
        var path = Path.Combine(_root, "state.json");
        var face = Place("ports", """{ "api": ["open"] }""");
        var state = new FaceRoomState(path);
        Assert.False(state.IsApproved(face));

        state.Approve(face);
        state.SetOpenFaces(["ports", "other"], "other");
        state.RecordOpened("ports", new DateTime(2026, 11, 15));
        state.RecordOpened("ports", new DateTime(2026, 11, 15));

        var reopened = new FaceRoomState(path);
        Assert.True(reopened.IsApproved(face));
        Assert.Equal(["ports", "other"], reopened.OpenFaces.Open);
        Assert.Equal("other", reopened.OpenFaces.Active);
        Assert.Equal(new DateTime(2026, 11, 15), reopened.LastOpened("ports"));

        var changed = Place("ports", """{ "api": ["open", "fs.read"] }""");
        Assert.False(reopened.IsApproved(changed));
    }

    [Fact]
    public void 三十日開かなかった面は枯れる_開けば戻る()
    {
        var state = new FaceRoomState(Path.Combine(_root, "state.json"));
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
        var face = Place("plain", "{}");
        var api = Api();

        foreach (var method in new[] { "workspace.folders", "workspace.activeFile", "open", "openUrl", "fs.read", "fs.list", "git.status", "run" })
            await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, method, new JsonObject { ["path"] = "a.txt" }));
        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "terminal.type"));
        Assert.Empty(_room.Calls);
    }

    [Fact]
    public async Task 読めるのはワークスペースの中だけ()
    {
        File.WriteAllText(Path.Combine(_workspace, "a.txt"), "中身");
        Directory.CreateDirectory(Path.Combine(_workspace, "sub"));
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "x");
        var face = Place("reader", """{ "api": ["fs.read", "open", "workspace"] }""");
        var api = Api();

        Assert.Equal("中身", (string?)await Call(api, face, "fs.read", new JsonObject { ["path"] = "a.txt" }));
        Assert.Equal("中身", (string?)await Call(api, face, "fs.read", new JsonObject { ["path"] = Path.Combine(_workspace, "a.txt") }));
        var listed = (JsonArray)(await Call(api, face, "fs.list", new JsonObject { ["path"] = _workspace }))!;
        Assert.Equal(["a.txt", "sub"], listed.Select(entry => (string)entry!["name"]!).Order());

        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "fs.read", new JsonObject { ["path"] = outside }));
        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "fs.read", new JsonObject { ["path"] = "..\\" + Path.GetFileName(_root) + "\\outside.txt" }));
        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "open", new JsonObject { ["path"] = outside }));
        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "openUrl", new JsonObject { ["url"] = "file:///C:/x" }));

        await Call(api, face, "open", new JsonObject { ["path"] = "a.txt", ["line"] = 3 });
        Assert.Equal([("open", Path.Combine(_workspace, "a.txt"), 3)], _room.Calls);
        var folders = (JsonArray)(await Call(api, face, "workspace.folders"))!;
        Assert.Equal(_workspace, (string)folders[0]!["path"]!);
    }

    [Fact]
    public async Task 置き場と通知は_口を挙げなくても使える()
    {
        var face = Place("notes", "{}");
        var api = Api();

        Assert.Null(await Call(api, face, "state.get"));
        await Call(api, face, "state.set", new JsonObject { ["value"] = new JsonObject { ["n"] = 2 } });
        Assert.Equal(2, (int)(await Call(api, face, "state.get"))!["n"]!);
        await Call(api, face, "state.set", new JsonObject { ["value"] = null });
        Assert.Null(await Call(api, face, "state.get"));
        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "state.set", []));

        await Call(api, face, "toast", new JsonObject { ["text"] = "できた" });
        Assert.Equal([("toast", "できた", 0)], _room.Calls);
    }

    [Fact]
    public async Task 回せるのは書いてあるコマンドだけ_行は固定で_渡した値は環境変数と変数に入る()
    {
        var face = Place("runner", """
            {
              "api": ["run"],
              "commands": {
                "echo": { "command": "Write-Output \"arg=$env:LOOMO_ARG_1 n=$env:LOOMO_ARGC\"; Write-Output \"in=$LoomoInput\"; exit 3", "timeoutSeconds": 30 },
                "where": { "command": "(Get-Location).Path", "cwd": "face" }
              }
            }
            """);
        var api = Api();
        var lines = new List<string>();

        var result = await Call(api, face, "run", new JsonObject
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

        var where = await Call(api, face, "run", new JsonObject { ["name"] = "where" });
        Assert.Contains(Path.Combine(_root, "runner"), (string)where!["output"]!, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "run", new JsonObject { ["name"] = "format-disk" }));
        await Assert.ThrowsAsync<FaceApiException>(() => Call(api, face, "run", new JsonObject { ["name"] = "Write-Output hi" }));
    }

    [Fact]
    public async Task 期限を過ぎたコマンドは止めて_そう言う()
    {
        var face = Place("slow", """{ "api": ["run"], "commands": { "sleep": { "command": "Start-Sleep -Seconds 60", "timeoutSeconds": 1 } } }""");

        var result = await Call(Api(), face, "run", new JsonObject { ["name"] = "sleep" });

        Assert.True((bool)result!["timedOut"]!);
        Assert.Equal(-1, (int)result["exitCode"]!);
    }

    // ── Claude から ─────────────────────────────────────────

    [Fact]
    public async Task 書き方の道具は_置き場所と口と_いまある面を返す()
    {
        Place("ports", """{ "title": "Ports", "description": "握っているポート" }""");
        _room.Guide = FaceGuide.Text(_store);
        var tools = new LoomoMcpTools(_room);

        var guide = await tools.CallAsync("face_guide", null);
        var shown = await tools.CallAsync("show_face", new JsonObject { ["id"] = "ports" });
        var missing = await tools.CallAsync("show_face", []);

        Assert.True(guide.Ok);
        Assert.Contains(_root, guide.Message);
        Assert.Contains("loomo.run(", guide.Message);
        Assert.Contains("- ports: Ports — 握っているポート", guide.Message);
        foreach (var name in FaceApi.All)
            Assert.Contains(name, guide.Message);
        Assert.True(shown.Ok);
        Assert.False(missing.Ok);
        Assert.Equal([("face", "ports", 0)], _room.Calls);
    }

    private sealed class FakeRoom : IFaceRoom, ILoomoRoomHost
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
        public string FaceGuide() => Guide;
        public string MotionGuide() => "";

        public Task<LoomoRoomResult> ShowFaceAsync(string id)
        {
            Calls.Add(("face", id, 0));
            return Task.FromResult(new LoomoRoomResult(true, "shown"));
        }
    }
}
