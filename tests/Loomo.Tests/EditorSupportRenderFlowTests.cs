using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// EditorSupport の描画本体。<b>ペインが固まる／固まらないを決めている分岐がここに全部ある</b>——
/// 言語サーバーが未準備・無応答・構造が空・コールドスタート、本文差し替えの可否。
/// <c>ShellWindow</c> の partial に埋まっている間はテストから1本も触れなかった経路。
///
/// <para>見ているのは<b>フレーム列</b>（何を、何回、どの順で出したか）。「途中で黙って return して
/// 何も出さない」＝画面が据え置きのまま、が固まるの正体なので、フレームが出ないことも含めて確かめる。</para>
/// </summary>
public class EditorSupportRenderFlowTests
{
    private const string File = @"C:\work\app\Foo.cs";

    // ── 言語サーバーが準備できていないとき ────────────────────────────────

    [Fact]
    public async Task 理由が分かるなら猶予を待たずに案内を出す()
    {
        // 未導入・起動失敗は待っても解消しない。待機文言を出し続けるのが最悪（§30.15）。
        var host = new FakeHost { Diagnosis = Notice("marksman が見つかりません") };
        var (flow, state) = Flow(host);

        var frames = await Render(flow, Request(lsp: null));

        Assert.Equal("marksman が見つかりません", Assert.IsType<EditorSupportFrameContent.NoticeContent>(
            Assert.Single(frames).Content).Notice.Message);
        Assert.True(host.ReadyRetryScheduled);   // 繋がる可能性はまだあるのでポーリングは続ける
        Assert.Null(state.OutlineRoots);
    }

    [Fact]
    public async Task 理由が分からないうちは案内を出さずに待つ()
    {
        // 起動直後の一瞬で「接続待ちです」を出すとちらつくので、猶予の間は画面を触らない。
        var (flow, _) = Flow(new FakeHost());

        var frames = await Render(flow, Request(lsp: null));

        Assert.Empty(frames);
    }

    [Fact]
    public async Task 猶予を過ぎたら接続待ちの案内を出す()
    {
        var host = new FakeHost();
        var (flow, state) = Flow(host);
        AdvanceReadyAttempts(state, EditorSupportRenderFlow.ConnectingNoticeGraceTicks);

        var frames = await Render(flow, Request(lsp: null));

        var notice = Assert.IsType<EditorSupportFrameContent.NoticeContent>(
            Assert.Single(frames).Content).Notice;
        Assert.Contains("接続待ち", notice.Message);
        Assert.False(notice.IsFailure);
    }

    [Fact]
    public async Task 別のファイルを開いているサーバーは準備できていない扱い()
    {
        // ハンドルが前のファイルのままなら、その構造を出すと「別ファイルの中身」が見える。
        var (flow, _) = Flow(new FakeHost { Diagnosis = Notice("x") });

        var frames = await Render(flow, Request(
            lsp: new FakeLspDocument(@"C:\work\app\Other.cs", [Method("Bar", 3)])));

        Assert.IsType<EditorSupportFrameContent.NoticeContent>(Assert.Single(frames).Content);
    }

    // ── 応答があるとき ────────────────────────────────────────────────

    [Fact]
    public async Task 構造を先に出してから呼び出しパネルを差し替える()
    {
        // ②の解析（references / callHierarchy）は遅い。待ってから出すと「開いたのに何も出ない」時間が伸びる。
        var (flow, state) = Flow(new FakeHost());

        var frames = await Render(flow, Request(
            lsp: new FakeLspDocument(File, [Method("Foo", 10)])));

        var outline = Assert.IsType<EditorSupportFrameContent.OutlineContent>(frames[0].Content);
        Assert.Single(outline.Roots);
        Assert.Empty(outline.Panels.References);          // 構造だけ先に出す
        // 2枚目は構造を作り直さず②だけ差し替える（＝ツリーの折りたたみが保たれる）。
        Assert.IsType<EditorSupportFrameContent.PanelsContent>(frames[1].Content);
        Assert.Equal(2, frames.Count);
        Assert.NotNull(state.OutlineRoots);
    }

    [Fact]
    public async Task 無応答は空アウトラインではなく期限切れの案内にする()
    {
        // 「シンボルが無い」と「返事が来ていない」を同じ空表示にすると、利用者に区別がつかない。
        var (flow, state) = Flow(new FakeHost());
        var lsp = new FakeLspDocument(File, symbols: null);   // 永久に返さない

        var frames = await Render(flow, Request(lsp: lsp), timeoutSafety: TimeSpan.FromSeconds(30));

        var notice = Assert.IsType<EditorSupportFrameContent.NoticeContent>(
            Assert.Single(frames).Content).Notice;
        Assert.True(notice.IsFailure);
        Assert.Null(state.OutlineRoots);
        Assert.Equal(1, lsp.DocumentSymbolRequests);   // 無応答サーバーへ取り直しを投げない
    }

    [Fact]
    public async Task コールドスタートで空だった構造は取り直す()
    {
        // サーバーが解析途中で空を返すことがある。1回で諦めると空のまま固まる。
        var (flow, _) = Flow(new FakeHost());
        var lsp = new FakeLspDocument(File, []) { LaterSymbols = [Method("Foo", 10)] };

        var frames = await Render(flow, Request(lsp: lsp));

        // 1枚目は「まだ何も無い」案内、最後に取り直した構造。
        Assert.IsType<EditorSupportFrameContent.NoticeContent>(frames[0].Content);
        Assert.Single(Assert.IsType<EditorSupportFrameContent.OutlineContent>(frames[^1].Content).Roots);
        Assert.True(lsp.DocumentSymbolRequests >= 2);
    }

    [Fact]
    public async Task 準備できたら準備待ちポーリングを止める()
    {
        var host = new FakeHost();
        var (flow, _) = Flow(host);

        await Render(flow, Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)])));

        Assert.True(host.ReadyRetryStopped);
        Assert.False(host.ReadyRetryScheduled);
    }

    // ── キャレット移動だけの差し替え ──────────────────────────────────

    [Fact]
    public async Task キャレットだけの要求は構造を作り直さない()
    {
        var (flow, state) = Flow(new FakeHost());
        var request = Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)]));
        await Render(flow, request);                     // 先に構造を出しておく
        var shownRoots = state.OutlineRoots;

        var frames = await Render(flow, request with { CaretLine = 30 }, EditorSupportUpdateReason.Caret);

        // ②パネルだけ差し替える＝ツリーは同じ実体のまま（折りたたみが飛ばない）。
        Assert.IsType<EditorSupportFrameContent.PanelsContent>(Assert.Single(frames).Content);
        Assert.Same(shownRoots, state.OutlineRoots);
    }

    [Fact]
    public async Task 同じシンボルの中で動いただけなら何も描かない()
    {
        // 打鍵のたびに②（references / callHierarchy）を取り直すと、遅い LSP で描画が詰まる。
        var workspace = new FakeLspWorkspace { HierarchyItem = Hierarchy("Foo", 10, 12) };
        var (flow, state) = Flow(new FakeHost(), workspace);
        var request = Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)]));
        await Render(flow, request);
        Assert.NotNull(state.CurrentSymbolRange);   // ②の対象シンボルの範囲を掴んでいる

        // 掴んだ範囲の中でキャレットが動いただけ（10行目 → 11行目）。
        var frames = await Render(flow, request with { CaretLine = 11 }, EditorSupportUpdateReason.Caret);

        Assert.Empty(frames);
    }

    // ── 提供者（Markdown 等）の経路 ───────────────────────────────────

    [Fact]
    public async Task WebViewを用意してから組み立てる()
    {
        // 逆順だと、Ensure の await で追い越されたときに「差し替えた本文が誰にも描かれない」が残る。
        var host = new FakeHost();
        var (flow, _) = Flow(host);

        await Render(flow, Request(file: @"C:\work\app\README.md", lsp: null));

        Assert.Equal(["EnsureWebView", "ReadyPageKey", "ClearFullPageRequest"], host.Calls);
    }

    [Fact]
    public async Task 復帰要求中は本文差し替えを使わない()
    {
        // 差し替え先のページが無いのに setBody を投げても何も起きない＝古い表示のまま固まる。
        var host = new FakeHost { ReadyPageKey = null };   // 復帰要求中はホストが null を返す
        var (flow, _) = Flow(host);

        var frames = await Render(flow, Request(file: @"C:\work\app\README.md", lsp: null));

        var web = Assert.IsType<EditorSupportFrameContent.WebContent>(Assert.Single(frames).Content);
        Assert.NotNull(web.Html);   // ページ全体が組み上がっている
        Assert.Null(web.Body);
    }

    [Fact]
    public async Task 復帰要求を下ろすのは適用の直前()
    {
        // 先に下ろすと、中断された描画が復帰要求を食い潰して、やり直しが本文差し替えへ戻ってしまう。
        var host = new FakeHost();
        var (flow, _) = Flow(host);

        await Render(flow, Request(file: @"C:\work\app\README.md", lsp: null));

        Assert.Equal("ClearFullPageRequest", host.Calls[^1]);
    }

    [Fact]
    public async Task 追い越された描画はフレームを出さない()
    {
        // ct 確認より後ろで UI を書くと、捨てられるはずの描画が画面へ残る（＝中途半端な表示）。
        var (flow, _) = Flow(new FakeHost());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var frames = new List<EditorSupportFrame>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.RenderAsync(
            Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)])),
            EditorSupportUpdateReason.Content, frames.Add, cts.Token));

        Assert.Empty(frames);
    }

    // ── 足場 ────────────────────────────────────────────────────────

    private static (EditorSupportRenderFlow Flow, EditorSupportController State) Flow(
        FakeHost host, FakeLspWorkspace? lspWorkspace = null)
    {
        var state = new EditorSupportController();
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(@"C:\work\app");
        var resolver = new EditorSupportResolver(
            new EditorSupportRegistry([new MarkdownEditorSupport(new LoomoSettings(), workspace)]),
            new CodeEditorSupport(), new HexEditorSupport());
        return (new EditorSupportRenderFlow(
            resolver, state, workspace, lspWorkspace ?? new FakeLspWorkspace(),
            new CodeEditorSupport(), host), state);
    }

    private static CallHierarchyItem Hierarchy(string name, int line0, int endLine0)
    {
        var range = new LspRange(new LspPosition(line0, 0), new LspPosition(endLine0, 1));
        return new CallHierarchyItem(name, (int)SymbolKind.Method, new Uri(File).AbsoluteUri, range, range);
    }

    private static EditorSupportRenderRequest Request(
        ILspDocument? lsp, string file = File, int caretLine = 10)
        => new(new EditorTab(Guid.NewGuid()), file, "class Foo { void Foo() {} }",
            caretLine, 0, lsp, "dark");

    private static async Task<List<EditorSupportFrame>> Render(
        EditorSupportRenderFlow flow,
        EditorSupportRenderRequest request,
        EditorSupportUpdateReason reason = EditorSupportUpdateReason.Content,
        TimeSpan? timeoutSafety = null)
    {
        var frames = new List<EditorSupportFrame>();
        using var cts = new CancellationTokenSource(timeoutSafety ?? TimeSpan.FromSeconds(20));
        await flow.RenderAsync(request, reason, frames.Add, cts.Token);
        return frames;
    }

    private static DocumentSymbol Method(string name, int line0)
    {
        var range = new LspRange(new LspPosition(line0, 0), new LspPosition(line0 + 2, 1));
        return new DocumentSymbol(name, SymbolKind.Method, range, range, null);
    }

    private static LspNoticeModel.Notice Notice(string message)
        => new(message, null, null, null, ".cs", false, false, true);

    /// <summary>準備待ちポーリングの経過 tick を進める（案内の猶予判定に使われる）。</summary>
    private static void AdvanceReadyAttempts(EditorSupportController state, int ticks)
    {
        state.ScheduleReadyRetry(TimeSpan.FromMinutes(5), (_, _) => { });
        for (var i = 0; i < ticks; i++)
            state.AdvanceReadyAttempt();
    }

    private sealed class FakeHost : IEditorSupportRenderHost
    {
        public List<string> Calls { get; } = [];
        public LspNoticeModel.Notice? Diagnosis { get; init; }
        public string? ReadyPageKeyValue { get; init; }
        public bool ReadyRetryScheduled { get; private set; }
        public bool ReadyRetryStopped { get; private set; }

        public string? ReadyPageKey
        {
            get { Calls.Add("ReadyPageKey"); return ReadyPageKeyValue; }
            init => ReadyPageKeyValue = value;
        }

        public Task EnsureWebViewAsync()
        {
            Calls.Add("EnsureWebView");
            return Task.CompletedTask;
        }

        public void ClearFullPageRequest() => Calls.Add("ClearFullPageRequest");
        public LspNoticeModel.Notice? DiagnoseLsp(string filePath) => Diagnosis;
        public void ScheduleLspReadyRetry() => ReadyRetryScheduled = true;
        public void StopLspReadyRetry() => ReadyRetryStopped = true;
    }
}
