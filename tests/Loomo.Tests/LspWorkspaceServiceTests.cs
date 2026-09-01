using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;
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
    private readonly FakeSolutionModelService _solution;
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
        _solution = new FakeSolutionModelService(
            new SolutionModel(null, "test", _root, [], ProjectLoadState.Ready));

        _sut = new LspWorkspaceService(_workspace, _servers, (def, root) =>
        {
            var client = new FakeLspClient(def.Executable, root);
            lock (_created) _created.Add(client);
            return client;
        }, _solution);
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

    /// <summary>
    /// ワークスペースフォルダーごとにサーバーを1本立てる。以前は1本へ全フォルダーを渡していたが、
    /// <c>workspaceFolders</c> が2件以上あると **Roslyn がデータフロー解析を要する
    /// リファクタリング（メソッドの抽出）を返さなくなる**（設計書 §32.4.4 の実測。
    /// [Loomo] なら5秒で2件、[Loomo, AimAssist] なら120秒待っても0件）。
    /// 構文だけのリファクタリングは出るので気付きにくい。
    /// </summary>
    [Fact]
    public async Task MultiRoot_StartsOneServerPerFolder()
    {
        var second = Path.Combine(Path.GetTempPath(), "loomo-lsp-ws2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        _workspace.AddFolder(second);

        var inSecond = Path.Combine(second, "B.cs");
        File.WriteAllText(inSecond, "class B {}");

        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        using var b = _sut.OpenDocument(inSecond, "class B {}")!;
        await Settle();

        Assert.Equal(2, Clients.Length);
        Assert.Contains(Clients, c => c.Root == _root);
        Assert.Contains(Clients, c => c.Root == second);
        // 各サーバーは自分のルートだけを受け取る。
        Assert.All(Clients, c => Assert.Equal([c.Root], c.LastWorkspaceFolders));

        Directory.Delete(second, recursive: true);
    }

    /// <summary>同じフォルダー内なら、何枚開いてもサーバーは1本のまま（§30 の本来の目的）。</summary>
    [Fact]
    public async Task SameFolder_StillSharesOneServer()
    {
        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        using var b = _sut.OpenDocument(Write("B.cs"), "class B {}")!;
        await Settle();

        Assert.Single(Clients);
        Assert.Equal(_root, Clients[0].Root);
    }

    [Fact]
    public void UnknownExtension_ReturnsNullWithoutStartingAnything()
    {
        Assert.Null(_sut.OpenDocument(Write("notes.zzz", "hi"), "hi"));
        Assert.Empty(Clients);
        Assert.False(_sut.IsServerAvailableFor(".zzz"));
        Assert.True(_sut.IsServerAvailableFor(".cs"));
    }

    /// <summary><c>initialize</c> に載せるのは**そのサーバーのルート1件だけ**。
    /// 他のワークスペースフォルダーを混ぜてはいけない（§32.4.4）。</summary>
    [Fact]
    public async Task Initialize_PassesOnlyItsOwnRoot()
    {
        var second = Path.Combine(Path.GetTempPath(), "loomo-lsp-ws3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        _workspace.AddFolder(second);

        using var a = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();

        Assert.Equal([_root], Clients[0].LastWorkspaceFolders);

        Directory.Delete(second, recursive: true);
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
    public async Task Pull_diagnostics_null_is_retried_without_clearing_existing_items()
    {
        using var document = _sut.OpenDocument(Write("A.cs"), "class A {}")!;
        await Settle();
        Clients[0].SupportsDocumentDiagnostics = true;
        Clients[0].DocumentDiagnostics = null;
        var existing = new[] { new LspDiagnostic(new(new(0, 0), new(0, 1)), "existing", DiagnosticSeverity.Error) };
        Clients[0].PublishDiagnostics(document.Uri, existing);

        document.UpdateText("class A { broken }");
        // 全体実行時は他のWPF／外部プロセステストでThreadPoolが混雑し、固定900msでは
        // 2回目のpull開始前を観測することがある。時間ではなく期待状態を待つ。
        for (var i = 0; i < 40 && Clients[0].DocumentDiagnosticRequestCount < 2; i++)
            await Task.Delay(100);

        Assert.True(Clients[0].DocumentDiagnosticRequestCount >= 2,
            $"診断pullの再試行が開始されませんでした。回数={Clients[0].DocumentDiagnosticRequestCount}");
        Assert.Equal(existing, document.CurrentDiagnostics);
    }

    [Fact]
    public async Task Pull_diagnostics_retries_past_transient_project_loading_cancellation()
    {
        var path = Write("A.cs");
        using var document = _sut.OpenDocument(path, "class A {}")!;
        await Settle();
        var client = Clients[0];
        client.SupportsDocumentDiagnostics = true;
        var expected = new[]
        {
            new LspDiagnostic(new(new(0, 6), new(0, 7)), "解析完了後の診断", DiagnosticSeverity.Warning),
        };
        client.DocumentDiagnosticsResponses.Enqueue(null);
        client.DocumentDiagnosticsResponses.Enqueue(null);
        client.DocumentDiagnosticsResponses.Enqueue(null);
        client.DocumentDiagnosticsResponses.Enqueue(expected);

        document.UpdateText("class A { broken }");
        // Task.Run の起動とテストホストの負荷で固定待機だけにすると、4回目のpull直前を
        // 観測してしまう。上限付きで期待状態を待ち、再試行回数そのものは厳密に確認する。
        for (var i = 0; i < 50 && client.DocumentDiagnosticRequestCount < 4; i++)
            await Task.Delay(100);

        Assert.Equal(4, client.DocumentDiagnosticRequestCount);
        Assert.Equal(expected, document.CurrentDiagnostics);
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
    public async Task SourceFixAll_MergesDuplicateWorkspaceEditsAcrossProjectFiles()
    {
        var a = Write("A.cs", "class A {}\n");
        var b = Write("B.cs", "class B {}\n");
        using var seed = _sut.OpenDocument(a, File.ReadAllText(a))!;
        await Settle();
        var client = Clients[0];
        var aUri = seed.Uri;
        var bUri = LspUri.FromPath(b);
        var edit = new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>
        {
            [aUri] = [new LspTextEdit(
                new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)), "// A\n")],
            [bUri] = [new LspTextEdit(
                new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)), "// B\n")],
        });
        client.CodeActionProvider = _ =>
        [
            new LspCodeAction("Fix all", LspCodeActionKinds.SourceFixAll, edit),
            new LspCodeAction("Unrelated quick fix", LspCodeActionKinds.QuickFix, edit),
        ];

        var result = await _sut.RequestSourceFixAllAsync([a, b]);

        Assert.NotNull(result.Edit);
        Assert.Equal(2, result.DocumentsScanned);
        Assert.Equal(2, result.ActionsFound);
        Assert.Single(result.Edit!.Changes[aUri]);
        Assert.Single(result.Edit.Changes[bUri]);
        Assert.Equal(2, client.CodeActionRequestCount);
    }

    [Fact]
    public async Task SourceFixAll_rejects_overlapping_edits_before_apply()
    {
        var a = Write("A.cs", "class A { int Value; }\n");
        using var seed = _sut.OpenDocument(a, File.ReadAllText(a))!;
        await Settle();
        var uri = seed.Uri;
        var client = Clients[0];
        client.CodeActionProvider = _ =>
        [
            new LspCodeAction("one", LspCodeActionKinds.SourceFixAll,
                new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>
                {
                    [uri] = [new LspTextEdit(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 5)), "class B")],
                })),
            new LspCodeAction("two", LspCodeActionKinds.SourceFixAll,
                new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>
                {
                    [uri] = [new LspTextEdit(
                        new LspRange(new LspPosition(0, 3), new LspPosition(0, 8)), "struct")],
                })),
        ];

        var result = await _sut.RequestSourceFixAllAsync([a]);

        Assert.Null(result.Edit);
        Assert.Contains("編集範囲が競合", result.Error);
    }

    [Fact]
    public async Task SourceFixAll_prefers_unsaved_text_for_an_open_document()
    {
        var path = Write("A.cs", "class A { int disk; }\n");
        using var document = _sut.OpenDocument(path, File.ReadAllText(path))!;
        await Settle();
        var client = Clients[0];
        client.CodeActionProvider = _ => [];

        var result = await _sut.RequestSourceFixAllAsync(
            [path], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = "class A { int unsaved; }\n",
            });

        Assert.Null(result.Error);
        Assert.Contains(client.Sent, notification =>
            notification.Kind == "didChange" && notification.Text.Contains("unsaved", StringComparison.Ordinal));
        Assert.Equal("class A { int unsaved; }\n", document.Text);
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
    public async Task ChangingSelectedTargetFramework_ReinitializesCSharpSessionAndReplaysDocuments()
    {
        var path = Write("A.cs");
        var projectPath = Path.Combine(_root, "App.csproj");
        var project = new ProjectModel("App", projectPath, _root, [],
            [
                new TargetFrameworkModel("net8.0", [], "latest",
                    [new ProjectItem("A.cs", path)], [], [], []),
                new TargetFrameworkModel("net9.0", [], "latest",
                    [new ProjectItem("A.cs", path)], [], [], []),
            ], "net8.0", false, ProjectLoadState.Ready);
        _solution.Publish(new SolutionModel(null, "test", _root, [project], ProjectLoadState.Ready));

        using var doc = _sut.OpenDocument(path, "class A {}")!;
        await Settle();
        Assert.Single(Clients);
        Clients[0].PublishDiagnostics(doc.Uri,
            [new LspDiagnostic(new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)),
                "old TFM diagnostic", DiagnosticSeverity.Warning, "StyleCop", "SA0001")]);
        Assert.Single(doc.CurrentDiagnostics);

        _solution.Publish(new SolutionModel(null, "test", _root,
            [project with { SelectedTargetFramework = "net9.0" }], ProjectLoadState.Ready));
        await Settle(1200);

        Assert.Equal(2, Clients.Length);
        var replacement = Clients.Single(c => !ReferenceEquals(c, Clients[0]));
        Assert.True(Clients[0].Disposed);
        Assert.Empty(doc.CurrentDiagnostics);
        Assert.Equal(1, replacement.CountOf("didOpen", doc.Uri));
        Assert.True(doc.IsReady);
    }

    [Fact]
    public async Task ChangingSelectedConfiguration_ReinitializesCSharpSessionAndReplaysDocuments()
    {
        var path = Write("A.cs");
        var projectPath = Path.Combine(_root, "App.csproj");
        var project = new ProjectModel("App", projectPath, _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("A.cs", path)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        _solution.Publish(new SolutionModel(null, "test", _root, [project], ProjectLoadState.Ready,
            Configurations: ["Debug", "Release"], SelectedConfiguration: "Debug"));

        using var doc = _sut.OpenDocument(path, "class A {}")!;
        await Settle();
        Assert.Single(Clients);

        _solution.Publish(new SolutionModel(null, "test", _root, [project], ProjectLoadState.Ready,
            Configurations: ["Debug", "Release"], SelectedConfiguration: "Release"));
        await Settle(1200);

        Assert.Equal(2, Clients.Length);
        var replacement = Clients.Single(c => !ReferenceEquals(c, Clients[0]));
        Assert.True(Clients[0].Disposed);
        Assert.Empty(doc.CurrentDiagnostics);
        Assert.Equal(1, replacement.CountOf("didOpen", doc.Uri));
        Assert.True(doc.IsReady);
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

    private sealed class FakeSolutionModelService(SolutionModel initial) : ISolutionModelService
    {
        public SolutionModel Current { get; private set; } = initial;
        public event EventHandler<SolutionModel>? Changed;

        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);

        public Task<bool> SelectTargetFrameworkAsync(string projectPath, string targetFramework,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public void Publish(SolutionModel model)
        {
            Current = model;
            Changed?.Invoke(this, model);
        }
    }
}
