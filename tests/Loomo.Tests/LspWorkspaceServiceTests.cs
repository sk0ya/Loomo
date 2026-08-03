using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// LSP セッション（設計書 §30）の中核＝プール共有・文書の参照カウント・書き手の一意化・
/// 診断のファンアウト・ワークスペース切替の検証。実プロセスは起動せず
/// <see cref="FakeLspClient"/> を注入する。
/// </summary>
public sealed class LspWorkspaceServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _storePath;
    private readonly FakeWorkspaceService _workspace = new();
    private readonly List<FakeLspClient> _created = [];
    private readonly LspServerTable _servers;
    private readonly LspWorkspaceService _sut;

    public LspWorkspaceServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "loomo-lsp-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _storePath = Path.Combine(_root, "lsp-servers.json");
        _workspace.OpenFolder(_root);

        _servers = new LspServerTable(_storePath);
        // 実行ファイルの実在に依存しないよう、テスト専用のサーバーを割り当てる。
        _servers.Set(".cs", new LspServerDef("fake-cs-server", [], "csharp"));
        _servers.Set(".py", new LspServerDef("fake-py-server", [], "python"));

        _sut = new LspWorkspaceService(_workspace, _servers, (def, root) =>
        {
            var client = new FakeLspClient(def.Executable, root);
            lock (_created) _created.Add(client);
            return client;
        });
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, string text = "// x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private FakeLspClient[] Clients { get { lock (_created) return _created.ToArray(); } }

    // ── プール共有 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SameRootAndExecutable_StartsOneServerForManyDocuments()
    {
        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        using var b = _sut.OpenDocument(Write("B.cs"), "class B {}")!;
        using var c = _sut.OpenDocument(Write("C.cs"), "class C {}")!;
        await Settle();

        Assert.NotNull(a);
        Assert.Single(Clients);
        Assert.Equal(1, Clients[0].InitializeCount);
        Assert.Equal(3, Clients[0].CountOf("didOpen"));
    }

    [Fact]
    public async Task DifferentExecutable_StartsItsOwnServer()
    {
        using var cs = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        using var py = _sut.OpenDocument(Write("a.py"), "x = 1")!;
        await Settle();

        Assert.Equal(2, Clients.Length);
        Assert.Contains(Clients, c => c.Executable == "fake-cs-server");
        Assert.Contains(Clients, c => c.Executable == "fake-py-server");
    }

    [Fact]
    public async Task MultiRoot_StillSharesOneServerAcrossFolders()
    {
        // 1本のサーバーが initialize で全フォルダーを受け取るので、フォルダーごとにプロセスを
        // 立てると同じ担当範囲の重複になる（実機で踏んだ）。ルートは常にプライマリ。
        var second = Path.Combine(_root, "sub2");
        Directory.CreateDirectory(second);
        _workspace.AddFolder(second);

        var inSecond = Path.Combine(second, "B.cs");
        File.WriteAllText(inSecond, "class B {}");

        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        using var b = _sut.OpenDocument(inSecond, "class B {}")!;
        await Settle();

        Assert.Single(Clients);
        Assert.Equal(_root, Clients[0].Root);
        Assert.Equal([_root, second], Clients[0].LastWorkspaceFolders);
    }

    [Fact]
    public void UnknownExtension_ReturnsNullWithoutStartingAnything()
    {
        Assert.Null(_sut.OpenDocument(Write("notes.zzz", "hi"), "hi"));
        Assert.Empty(Clients);
        Assert.False(_sut.IsServerAvailableFor(".zzz"));
        Assert.True(_sut.IsServerAvailableFor(".cs"));
    }

    [Fact]
    public async Task Initialize_PassesEveryWorkspaceFolder()
    {
        var second = Path.Combine(_root, "sub2");
        Directory.CreateDirectory(second);
        _workspace.AddFolder(second);

        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();

        Assert.Equal([_root, second], Clients[0].LastWorkspaceFolders);
    }

    [Fact]
    public async Task Runtime_status_distinguishes_project_loading_and_ready()
    {
        using var document = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();

        var loading = Assert.Single(_sut.ServerStatuses);
        Assert.Equal("fake-cs-server", loading.Executable);
        Assert.Equal(LspServerRuntimeState.ProjectLoading, loading.State);

        Clients[0].PublishDiagnostics(document.Uri, []);
        await Settle();

        Assert.Equal(LspServerRuntimeState.Ready, Assert.Single(_sut.ServerStatuses).State);
    }

    [Fact]
    public async Task Pull_diagnostics_null_is_retried_once_without_clearing_existing_items()
    {
        using var document = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();
        Clients[0].SupportsDocumentDiagnostics = true;
        Clients[0].DocumentDiagnostics = null;
        var existing = new[] { new LspDiagnostic(new(new(0, 0), new(0, 1)), "existing", DiagnosticSeverity.Error) };
        Clients[0].PublishDiagnostics(document.Uri, existing);

        document.UpdateText("class A { broken }");
        await Task.Delay(900);

        Assert.Equal(2, Clients[0].DocumentDiagnosticRequestCount);
        Assert.Equal(existing, document.CurrentDiagnostics);
    }

    [Fact]
    public async Task Manual_restart_reuses_crash_recovery_and_replays_open_documents()
    {
        using var document = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();
        // 実プロセスはDispose直後もしばらくIsRunning=trueになり得る。
        Clients[0].KeepRunningAfterDispose = true;
        Clients[0].RaiseExitedOnDispose = true;

        Assert.True(_sut.RestartServer("fake-cs-server"));
        await Task.Delay(700);
        await Settle();

        Assert.Equal(2, Clients.Length);
        Assert.Equal(1, Clients[1].CountOf("didOpen"));
        Assert.True(document.IsReady);
    }

    [Fact]
    public async Task Closing_last_handle_publishes_empty_snapshot_to_clear_problems()
    {
        var document = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();
        IReadOnlyList<LspDiagnostic>? published = null;
        _sut.DiagnosticsPublished += (uri, diagnostics) =>
        {
            if (uri == document.Uri) published = diagnostics;
        };

        document.Dispose();
        await Settle();

        Assert.NotNull(published);
        Assert.Empty(published!);
    }

    // ── 参照カウントと書き手の一意化（§30.3.4） ─────────────────────────────

    [Fact]
    public async Task TwoHandlesOnOneUri_OpenOnceAndOnlyTheFirstIsWriter()
    {
        var path = Write("A.cs", "v1");
        var first = _sut.OpenDocument(path, "v1")!;
        var second = _sut.OpenDocument(path, "v1")!;
        await Settle();

        Assert.Equal(1, Clients[0].CountOf("didOpen"));
        Assert.True(first.IsWriter);
        Assert.False(second.IsWriter);

        second.UpdateText("from reader");    // 読み手 → no-op
        first.UpdateText("from writer");
        await Settle();

        var changes = Clients[0].Sent.Where(n => n.Kind == "didChange").ToList();
        Assert.Single(changes);
        Assert.Equal("from writer", changes[0].Text);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task DisposingOneOfTwoHandles_DoesNotCloseTheDocument()
    {
        var path = Write("A.cs");
        var first = _sut.OpenDocument(path, "v1")!;
        var second = _sut.OpenDocument(path, "v1")!;
        await Settle();

        first.Dispose();
        await Settle();
        Assert.Equal(0, Clients[0].CountOf("didClose"));

        second.Dispose();
        await Settle();
        Assert.Equal(1, Clients[0].CountOf("didClose"));
    }

    [Fact]
    public async Task DisposingTheWriter_PromotesTheNextHandleAndResyncsText()
    {
        var path = Write("A.cs");
        var first = _sut.OpenDocument(path, "v1")!;
        var second = _sut.OpenDocument(path, "v1")!;
        await Settle();

        first.UpdateText("edited by first");
        await Settle();
        first.Dispose();
        await Settle();

        Assert.True(second.IsWriter);
        // 移譲時に現在の正本を送り直す（読み手側の表示と食い違わないように）。
        var last = Clients[0].Sent.Last(n => n.Kind == "didChange");
        Assert.Equal("edited by first", last.Text);

        second.UpdateText("edited by second");
        await Settle();
        Assert.Equal("edited by second", Clients[0].Sent.Last(n => n.Kind == "didChange").Text);

        second.Dispose();
    }

    [Fact]
    public async Task Diagnostics_ReachEveryHandleOnTheUri()
    {
        var path = Write("A.cs");
        using var first = _sut.OpenDocument(path, "v1")!;
        using var second = _sut.OpenDocument(path, "v1")!;
        await Settle();

        IReadOnlyList<LspDiagnostic>? onFirst = null, onSecond = null;
        string? publishedUri = null;
        first.DiagnosticsChanged += d => onFirst = d;
        second.DiagnosticsChanged += d => onSecond = d;
        _sut.DiagnosticsPublished += (uri, _) => publishedUri = uri;

        var diagnostics = new[]
        {
            new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)),
                "boom", DiagnosticSeverity.Error),
        };
        Clients[0].PublishDiagnostics(first.Uri, diagnostics);

        Assert.Same(diagnostics, onFirst);
        Assert.Same(diagnostics, onSecond);   // 読み手のビューにも波線が出る
        Assert.Equal(first.Uri, publishedUri);
        Assert.Equal(diagnostics, first.CurrentDiagnostics);
    }

    [Fact]
    public void FailedProcessStart_IsVisibleAsAFailedServerInsteadOfSilence()
    {
        // 起動失敗はログに落ちるだけで、UI には「接続待ち」しか出なかった（実機で踏んだ：
        // npm の .cmd シムを素の名前で起動しようとして毎回 Win32Exception）。
        using var sut = new LspWorkspaceService(_workspace, _servers,
            (_, _) => throw new System.ComponentModel.Win32Exception(2, "指定されたファイルが見つかりません。"));

        Assert.Null(sut.OpenDocument(Write("A.cs"), "class A {}"));

        var status = Assert.Single(sut.ServerStatuses);
        Assert.Equal("fake-cs-server", status.Executable);
        Assert.Equal(LspServerRuntimeState.Failed, status.State);
        Assert.Contains("起動に失敗", status.LastError);
    }

    [Fact]
    public async Task Diagnostics_ReachTheDocumentEvenWhenTheServerEncodesTheDriveColon()
    {
        // typescript-language-server は didOpen で送った "file:///C:/…" ではなく
        // "file:///c%3A/…" で publishDiagnostics を返す。URI を素の文字列で引いていた頃は
        // ここで取りこぼし、TypeScript だけ波線が一切出なかった。
        var path = Write("A.cs");
        using var document = _sut.OpenDocument(path, "class A {}")!;
        await Settle();

        IReadOnlyList<LspDiagnostic>? received = null;
        document.DiagnosticsChanged += d => received = d;

        var diagnostics = new[]
        {
            new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)),
                "boom", DiagnosticSeverity.Error),
        };
        Clients[0].PublishDiagnostics(EncodeDriveColon(document.Uri), diagnostics);

        Assert.Same(diagnostics, received);
        Assert.Equal(diagnostics, document.CurrentDiagnostics);
    }

    /// <summary>"file:///C:/x" → "file:///c%3A/x"（vscode-uri 系サーバーの綴り）。</summary>
    private static string EncodeDriveColon(string uri) =>
        System.Text.RegularExpressions.Regex.Replace(
            uri, @"^file:///([A-Za-z]):", m => $"file:///{m.Groups[1].Value.ToLowerInvariant()}%3A");

    [Fact]
    public async Task PullDiagnostics_AfterTextChangeReachDocumentAndWorkspaceSubscribers()
    {
        var path = Write("A.cs");
        using var document = _sut.OpenDocument(path, "class A {}")!;
        await Settle();
        var client = Clients[0];
        client.SupportsDocumentDiagnostics = true;
        client.DocumentDiagnostics =
        [
            new LspDiagnostic(
                new LspRange(new LspPosition(0, 6), new LspPosition(0, 7)),
                "識別子が必要です", DiagnosticSeverity.Error, "compiler")
        ];
        IReadOnlyList<LspDiagnostic>? received = null;
        string? publishedUri = null;
        document.DiagnosticsChanged += diagnostics => received = diagnostics;
        _sut.DiagnosticsPublished += (uri, _) => publishedUri = uri;

        document.UpdateText("class # {}");
        await Settle(600);

        Assert.Equal(1, client.DocumentDiagnosticRequestCount);
        Assert.Equal("識別子が必要です", Assert.Single(received!).Message);
        Assert.Equal(document.Uri, publishedUri);
        Assert.Equal(received, document.CurrentDiagnostics);
        Assert.Equal(LspServerRuntimeState.Ready, Assert.Single(_sut.ServerStatuses).State);
    }

    // ── ワークスペーススコープ ─────────────────────────────────────────────

    [Fact]
    public async Task WorkspaceSymbols_WorkWithNoDocumentOpen()
    {
        // .sln があるので C# のワークスペースだと判る → 文書ゼロでもサーバーを起こす。
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        var symbol = new LspSymbolInformation(
            "Widget", SymbolKind.Class,
            new LspLocation("file:///x/Widget.cs",
                new LspRange(new LspPosition(3, 0), new LspPosition(3, 6))));

        var task = _sut.GetWorkspaceSymbolsAsync("Widget", isClass: true);
        await Settle();
        Clients.Single().WorkspaceSymbols.Add(symbol);
        // 起動と同時に結果を用意できないので、起動後にもう一度問い合わせる。
        await task;
        var results = await _sut.GetWorkspaceSymbolsAsync("Widget", isClass: true);

        Assert.Single(Clients);
        Assert.Equal("Widget", Assert.Single(results).Name);
    }

    [Fact]
    public async Task WorkspaceSymbols_DeduplicateAcrossServers()
    {
        using var cs = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        using var py = _sut.OpenDocument(Write("a.py"), "x = 1")!;
        await Settle();

        var shared = new LspSymbolInformation(
            "Dup", SymbolKind.Class,
            new LspLocation("file:///x/Dup.cs",
                new LspRange(new LspPosition(1, 0), new LspPosition(1, 3))));
        foreach (var c in Clients) c.WorkspaceSymbols.Add(shared);

        var results = await _sut.GetWorkspaceSymbolsAsync("Dup", isClass: true);

        Assert.Single(results);
    }

    // ── ライフサイクル ────────────────────────────────────────────────────

    [Fact]
    public async Task ChangingWorkspaceFolders_ShutsServersDownImmediately()
    {
        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();
        Assert.False(Clients[0].Disposed);

        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        _workspace.OpenFolder(other);
        await Settle();

        Assert.True(Clients[0].Disposed);
    }

    [Fact]
    public async Task ChangingTheServerForAnExtension_ReopensOpenDocumentsInPlace()
    {
        var path = Write("A.cs");
        using var doc = _sut.OpenDocument(path, "class A {}")!;
        await Settle();
        Assert.Single(Clients);

        // 設定画面／:LspAdd 相当。開き直しを要求せずに新しいサーバーへ載せ替わること。
        _servers.Set(".cs", new LspServerDef("fake-cs-server-v2", [], "csharp"));
        await Settle();

        Assert.Equal(2, Clients.Length);
        var replacement = Clients.Single(c => c.Executable == "fake-cs-server-v2");
        Assert.Equal(1, replacement.CountOf("didOpen", doc.Uri));
        Assert.Equal(1, Clients[0].CountOf("didClose", doc.Uri));
    }

    [Fact]
    public async Task ServerCrash_ReconnectsAndReplaysTheLatestText()
    {
        var path = Write("A.cs");
        using var doc = _sut.OpenDocument(path, "v1")!;
        await Settle();
        doc.UpdateText("v2");
        await Settle();

        Clients[0].Kill();
        await Settle(1200);   // 再接続はバックオフ 0.5s 後

        Assert.Equal(2, Clients.Length);
        var restarted = Clients[1];
        var replay = restarted.Sent.Single(n => n.Kind == "didOpen");
        Assert.Equal("v2", replay.Text);   // クラッシュ前の最新テキストで開き直す
        Assert.True(doc.IsReady);
    }

    /// <summary>バックグラウンドで進む didOpen/didChange/再接続を待ち合わせる。</summary>
    private static Task Settle(int ms = 150) => Task.Delay(ms);
}
