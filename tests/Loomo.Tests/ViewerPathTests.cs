using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// パスから開く Viewer（§44.2）。パスの見せ方の決め方（頁・プログラム・作る・無い）／タブの並びに保存する鍵／作った Viewer の id
/// （拡張子ごと）／<c>target.*</c> の線引き（渡したパス 1 つだけ・挙げていなければ断る）／頁のオリジン／作らせる頼みごと／TABS の行。
/// 画面（WebView2・窓の重ね合わせ）は対象外。
/// </summary>
public sealed class ViewerPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-viewerpath-{Guid.NewGuid():N}");
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"loomo-viewerpath-files-{Guid.NewGuid():N}");
    private readonly ViewerStore _store;

    public ViewerPathTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_files);
        _store = new ViewerStore(_root);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _files })
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    private string Put(string name, string content = "x")
    {
        var path = Path.Combine(_files, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── 見せ方 ───────────────────────────────────────────────

    [Fact]
    public void 見せ方_HTMLは頁_exeはプログラム_URLはブラウザ_無ければ無い()
    {
        Assert.Equal(ViewerTargetKind.Page, ViewerTargets.Classify(Put("a.html"), resolver: null).Kind);
        Assert.Equal(ViewerTargetKind.Program, ViewerTargets.Classify(Put("tool.EXE"), resolver: null).Kind);
        Assert.Equal(ViewerTargetKind.Missing, ViewerTargets.Classify(Path.Combine(_files, "none.txt"), resolver: null).Kind);
        var web = ViewerTargets.Classify(" https://example.com/a?b=1 ", resolver: null);
        Assert.Equal(ViewerTargetKind.Web, web.Kind);
        Assert.Equal("https://example.com/a?b=1", web.Path);
        Assert.Null(ViewerTargets.WebUrl("ftp://example.com"));
    }

    [Fact]
    public void 見せ方_決まっていないものはエディタ_その拡張子のViewerを作ってあればそれ()
    {
        var path = Put("data.weird", "a,b");
        var text = ViewerTargets.Classify(path, resolver: null, _store);
        Assert.Equal(ViewerTargetKind.Text, text.Kind);
        Assert.Equal("file-weird", text.ViewerId);

        Place("file-weird", """{ "api": ["target"] }""");
        var generated = ViewerTargets.Classify(path, resolver: null, _store);
        Assert.Equal(ViewerTargetKind.Generated, generated.Kind);
        Assert.Equal("file-weird", generated.ViewerId);
    }

    [Fact]
    public void 見せ方_indexhtmlのあるフォルダーは頁_無いフォルダーはフォルダー用のViewer()
    {
        Put(Path.Combine("site", "index.html"));
        var site = ViewerTargets.Classify(Path.Combine(_files, "site"), resolver: null);
        Assert.Equal(ViewerTargetKind.Page, site.Kind);
        Assert.Equal(Path.Combine(_files, "site", "index.html"), site.Page);

        Put(Path.Combine("plain", "a.txt"));
        var plain = ViewerTargets.Classify(Path.Combine(_files, "plain"), resolver: null);
        Assert.Equal(ViewerTargetKind.Generated, plain.Kind);
        Assert.Equal("file-folder", plain.ViewerId);
    }

    [Fact]
    public void 入力の正規化_引用符と末尾の区切り_file_URL_相対パスは受けない()
    {
        Assert.Equal(@"C:\work\a.pdf", ViewerTargets.Normalize("  \"C:\\work\\a.pdf\"  "));
        Assert.Equal(@"C:\work", ViewerTargets.Normalize(@"C:\work\"));
        Assert.Equal(@"C:\", ViewerTargets.Normalize(@"C:\"));
        Assert.Equal(@"C:\work\a b.pdf", ViewerTargets.Normalize("file:///C:/work/a%20b.pdf"));
        Assert.Null(ViewerTargets.Normalize(@"work\a.pdf"));
        Assert.Null(ViewerTargets.Normalize("   "));
    }

    [Theory]
    [InlineData(@"C:\a\data.SQLite", false, "file-sqlite")]
    [InlineData(@"C:\a\Makefile", false, "file-noext")]
    [InlineData(@"C:\a\x.c++", false, "file-c")]
    [InlineData(@"C:\a\folder", true, "file-folder")]
    public void 作るViewerのidは拡張子ごと(string path, bool isDirectory, string expected)
    {
        var id = ViewerPath.IdFor(path, isDirectory);
        Assert.Equal(expected, id);
        Assert.True(ViewerManifest.IsValidId(id));
        Assert.True(ViewerPath.IsGenerated(id));
    }

    [Fact]
    public void 並びの鍵_パスは前置きつき_相対パスは鍵にならない_開いている並びに残る()
    {
        var key = ViewerPath.KeyOf(@"C:\work\a.pdf");
        Assert.True(ViewerPath.TryParseKey(key, out var path));
        Assert.Equal(@"C:\work\a.pdf", path);
        Assert.False(ViewerPath.TryParseKey("path:work\\a.pdf", out _));
        Assert.False(ViewerPath.TryParseKey("ports", out _));
        Assert.True(ViewerPath.TryParseUrlKey(ViewerPath.UrlKeyOf("https://example.com/x"), out var url));
        Assert.Equal("https://example.com/x", url);
        Assert.False(ViewerPath.TryParseUrlKey("url:file:///C:/a", out _));

        var state = new ViewerRoomState(Path.Combine(_root, "state.json"));
        state.SetOpenViewers(["ports", key, "path:relative", "Bad Id", "url:https://example.com/"], key);
        var (open, active) = state.OpenViewers;
        Assert.Equal(["ports", key, "url:https://example.com/"], open);
        Assert.Equal(key, active);
    }

    // ── target.* ─────────────────────────────────────────────

    private ViewerManifest Place(string id, string json)
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "viewer.json"), json);
        File.WriteAllText(Path.Combine(folder, "index.html"), "<p>x</p>");
        return _store.Find(id).Manifest!;
    }

    private ViewerRoomApi Api() => new(_store, new FakeWorkspaceService(_files), new NoRoom());

    private static Task<JsonNode?> Call(ViewerRoomApi api, ViewerManifest viewer, string method, string? target)
        => api.CallAsync(viewer, method, null, (_, _) => { }, CancellationToken.None, target);

    [Fact]
    public async Task target_渡したパスだけを読む_許しは要らない()
    {
        var viewer = Place("file-log", """{ "api": ["target"] }""");
        Assert.False(viewer.NeedsApproval);
        var outside = Path.Combine(Path.GetTempPath(), $"loomo-outside-{Guid.NewGuid():N}.log");
        File.WriteAllText(outside, "こんにちは\nworld");
        try
        {
            var api = Api();
            Assert.Equal("こんにちは\nworld", (string?)await Call(api, viewer, "target.read", outside));
            var info = (await Call(api, viewer, "target.info", outside))!.AsObject();
            Assert.Equal(Path.GetFileName(outside), (string?)info["name"]);
            Assert.False((bool)info["isDirectory"]!);
            var bytes = Convert.FromBase64String((string)(await Call(api, viewer, "target.bytes", outside))!);
            Assert.Equal(File.ReadAllBytes(outside), bytes);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task target_挙げていない_ファイルのために開いていない_フォルダーにreadは断る()
    {
        var api = Api();
        var file = Put("a.log", "x");
        var without = Place("plain", """{ "api": [] }""");
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, without, "target.read", file));

        var with = Place("file-log", """{ "api": ["target"] }""");
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, with, "target.read", target: null));
        await Assert.ThrowsAsync<ViewerApiException>(() => Call(api, with, "target.read", _files));
        var listed = (await Call(api, with, "target.list", _files))!.AsArray();
        Assert.Contains(listed, entry => (string?)entry!["name"] == "a.log");
    }

    // ── 頁・頼みごと ─────────────────────────────────────────

    [Fact]
    public void 頁のオリジンはフォルダーごと_URLは区切りごとにエスケープ()
    {
        var a = ViewerPageTab.HostFor(@"C:\site");
        Assert.Equal(a, ViewerPageTab.HostFor(@"c:\SITE"));
        Assert.NotEqual(a, ViewerPageTab.HostFor(@"C:\other"));
        Assert.EndsWith(".page.loomo", a);
        Assert.Equal($"https://{a}/docs/a%20b.html", ViewerPageTab.UrlFor(a, @"C:\site", @"C:\site\docs\a b.html"));
    }

    [Fact]
    public void 作らせる頼みごと_置き場と_id_ファイルの頭_作り直しの一言が入る()
    {
        var csv = Put("data.weird", "a,b\n1,2");
        var prompt = ViewerGenerator.Prompt(_store, csv, "file-weird", note: "列を色分けして");
        Assert.Contains(Path.Combine(_root, "file-weird"), prompt);
        Assert.Contains("\"file-weird\"", prompt);
        Assert.Contains("a,b\n1,2", prompt);
        Assert.Contains("列を色分けして", prompt);
        Assert.Contains("loomo.target", prompt);

        var binary = Path.Combine(_files, "blob.bin");
        File.WriteAllBytes(binary, [0x53, 0x51, 0x00, 0x4C]);
        Assert.Contains("53 51 00 4C", ViewerGenerator.Prompt(_store, binary, "file-bin", note: null));
    }

    [Fact]
    public async Task 作らせる_作業フォルダーはそのViewerのフォルダーだけ_書けたら成功()
    {
        var target = Put("talk.srt", "1");
        string? workedIn = null;
        var result = await ViewerGenerator.GenerateAsync(_store, target, "file-srt", null, (folder, _, _) =>
        {
            workedIn = folder;
            File.WriteAllText(Path.Combine(folder, "viewer.json"), """{ "api": ["target"] }""");
            File.WriteAllText(Path.Combine(folder, "index.html"), "<p>srt</p>");
            return Task.FromResult((0, "done"));
        }, CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(_store.FolderOf("file-srt"), workedIn);
    }

    [Fact]
    public async Task 作らせる_途中で終わった_作り直しで何も変わらなければ失敗と言う()
    {
        var target = Put("talk.srt", "1");
        var failed = await ViewerGenerator.GenerateAsync(_store, target, "file-srt", null,
            (_, _, _) => Task.FromResult((1, "not logged in")), CancellationToken.None);
        Assert.False(failed.Ok);
        Assert.Contains("not logged in", failed.Message);

        Place("file-srt", """{ "api": ["target"] }""");   // 作り直し：古い定義と頁がもうある
        var unchanged = await ViewerGenerator.GenerateAsync(_store, target, "file-srt", "直して",
            (_, _, _) => Task.FromResult((0, "nothing to do")), CancellationToken.None);
        Assert.False(unchanged.Ok);
        Assert.Contains("書き換わりませんでした", unchanged.Message);
    }

    // ── TABS ────────────────────────────────────────────────

    [Fact]
    public void TABSの行_並び_題_前に出ているタブを写し_消えたタブは外す()
    {
        var tabs = new TabsViewModel();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        tabs.SetViewerTabs([new ViewerTabRow(a, "a.pdf", @"C:\a.pdf", @"C:\a.pdf", true), new ViewerTabRow(b, "Ports", "ports", null, false)]);
        Assert.Equal(["a.pdf", "Ports"], tabs.ViewerTabs.Select(t => t.Title));
        Assert.All(tabs.ViewerTabs, t => Assert.Equal(TabEntryKind.Viewer, t.Kind));
        Assert.False(tabs.ViewerTabs[0].CanShelve);
        Assert.False(tabs.ViewerTabs[0].CanOpenInOtherRoom);
        Assert.Equal(2, tabs.TotalCount);

        tabs.SetViewerTabs([new ViewerTabRow(b, "Ports", "ports", null, true)]);
        var only = Assert.Single(tabs.ViewerTabs);
        Assert.Equal(b, only.Id);
        Assert.True(only.IsActive);
    }

    [Fact]
    public void 閉じる範囲_Viewerのタブにも効く()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var plan = WorkspaceTabClosePolicy.CreatePlan(TabEntryKind.Viewer, b, WorkspaceTabCloseScope.Others,
            [], [], [], [a, b, c]);
        Assert.Equal([a, c], plan.TabIds);
    }

    private sealed class NoRoom : IViewerRoom
    {
        public Task<JsonNode?> ActiveFileAsync() => Task.FromResult<JsonNode?>(null);
        public Task<bool> OpenFileAsync(string path, int line) => Task.FromResult(false);
        public Task<bool> OpenUrlAsync(string url) => Task.FromResult(false);
        public Task<JsonNode> GitStatusAsync() => Task.FromResult<JsonNode>(new JsonArray());
        public void Toast(string text) { }
    }
}
