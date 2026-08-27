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
    private const string Other = @"C:\work\app\Other.cs";

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
    public async Task 使用箇所の検索は宣言自身を含めない()
    {
        var lsp = new FakeLspDocument(File, [Method("Foo", 10)])
        {
            References =
            [
                new LspLocation(
                    new Uri(File).AbsoluteUri,
                    new LspRange(new LspPosition(20, 4), new LspPosition(20, 7)))
            ]
        };
        var (flow, _) = Flow(new FakeHost());

        var frames = await Render(flow, Request(lsp));

        Assert.False(lsp.LastIncludeDeclaration);
        var panels = Assert.IsType<EditorSupportFrameContent.PanelsContent>(frames[1].Content);
        var reference = Assert.Single(panels.Panels.References);
        Assert.Equal(20, reference.Line0);
        Assert.Equal(4, reference.Column0); // 使用箇所の実体位置へ着地する
    }

    [Fact]
    public async Task 呼び出し元と呼び出し先のシンボル位置をパネルへ渡す()
    {
        // 行頭ではなく名前の位置へジャンプできることを、LSP応答からCallReferenceまで通して固定する。
        var callerRange = new LspRange(
            new LspPosition(30, 0), new LspPosition(32, 1));
        var callerSelection = new LspRange(
            new LspPosition(30, 8), new LspPosition(30, 14));
        var calleeRange = new LspRange(
            new LspPosition(40, 0), new LspPosition(42, 1));
        var calleeSelection = new LspRange(
            new LspPosition(40, 12), new LspPosition(40, 18));
        var workspace = new FakeLspWorkspace
        {
            HierarchyItem = Hierarchy("Foo", 10, 12),
            Incoming =
            [
                new CallHierarchyIncomingCall(
                    new CallHierarchyItem(
                        "Caller", (int)SymbolKind.Method,
                        new Uri(@"C:\work\Caller.cs").AbsoluteUri,
                        callerRange, callerSelection),
                    [])
            ],
            Outgoing =
            [
                new CallHierarchyOutgoingCall(
                    new CallHierarchyItem(
                        "Callee", (int)SymbolKind.Method,
                        new Uri(@"C:\work\Callee.cs").AbsoluteUri,
                        calleeRange, calleeSelection),
                    [])
            ],
        };
        var (flow, _) = Flow(new FakeHost(), workspace);

        var frames = await Render(flow, Request(
            new FakeLspDocument(File, [Method("Foo", 10)])));

        var panels = Assert.IsType<EditorSupportFrameContent.PanelsContent>(frames[1].Content);
        var incoming = Assert.Single(panels.Panels.Incoming);
        Assert.Equal("Caller", incoming.Symbol);
        Assert.Equal(30, incoming.Line0);
        Assert.Equal(8, incoming.Column0);

        var outgoing = Assert.Single(panels.Panels.Outgoing);
        Assert.Equal("Callee", outgoing.Symbol);
        Assert.Equal(40, outgoing.Line0);
        Assert.Equal(12, outgoing.Column0);
    }

    [Fact]
    public async Task メソッド以外では呼び出し階層を展開しない()
    {
        // Roslyn がプロパティにも prepareCallHierarchy の項目を返すことがある。
        // それを incoming/outgoing へ渡すとサーバー内部例外になるため、C# の回帰として固定する。
        var workspace = new FakeLspWorkspace
        {
            HierarchyItem = new CallHierarchyItem(
                "Value", (int)SymbolKind.Property, new Uri(File).AbsoluteUri,
                new LspRange(new LspPosition(10, 0), new LspPosition(12, 1)),
                new LspRange(new LspPosition(10, 6), new LspPosition(10, 11))),
            Incoming =
            [
                new CallHierarchyIncomingCall(
                    Hierarchy("Caller", 30, 32), [])
            ],
            Outgoing =
            [
                new CallHierarchyOutgoingCall(
                    Hierarchy("Callee", 40, 42), [])
            ],
        };
        var (flow, _) = Flow(new FakeHost(), workspace);

        var frames = await Render(flow, Request(
            new FakeLspDocument(File, [Method("Value", 10)])));

        var panels = Assert.IsType<EditorSupportFrameContent.PanelsContent>(frames[1].Content);
        Assert.Null(panels.Panels.Target);
        Assert.Empty(panels.Panels.Incoming);
        Assert.Empty(panels.Panels.Outgoing);
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

    [Fact]
    public async Task 非コード表示ではキャレット移動で本文を再変換しない()
    {
        // Markdown/JSON 等はキャレットを表示へ反映しない。ここで提供者経路へ進むと、
        // カーソル移動のたびに本文全体の変換と WebView2 の本文差し替えが発生する。
        var host = new FakeHost();
        var (flow, _) = Flow(host);
        var frames = await Render(flow, Request(
            lsp: null, file: @"C:\work\app\README.md"), EditorSupportUpdateReason.Caret);

        Assert.Empty(frames);
        Assert.Empty(host.Calls);
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
    public async Task WebViewを用意できないときは何も出さない()
    {
        // 載せる先が無いのにフレームを出すと、ヘッダーとタイトルだけ新しいファイルへ変わって
        // 中身は前のファイルのまま、という半端な画面が残る（＝固まったようにしか見えない）。
        var host = new FakeHost { WebViewAvailable = false };
        var (flow, _) = Flow(host);

        var frames = await Render(flow, Request(file: @"C:\work\app\README.md", lsp: null));

        Assert.Empty(frames);
        Assert.Equal(["EnsureWebView"], host.Calls);   // 組み立てへ進まない
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

    /// <summary>
    /// 追い越されて捨てられた描画が<b>アウトライン状態だけ</b>書き換えてしまう経路の回帰。
    /// フレームは出ない（＝画面は変わらない）のに状態が別ファイルのものに変わると、以後
    /// <c>ShouldRefreshCallPanels</c> が食い違い続け、<b>ツリーは出ているのに②が二度と更新されない</b>。
    /// 画面と状態は必ず同時に動くこと。
    /// </summary>
    [Fact]
    public async Task 追い越された描画はアウトライン状態も書き換えない()
    {
        var (flow, state) = Flow(new FakeHost());
        await Render(flow, Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)])));
        var shownRoots = state.OutlineRoots;
        var shownFile = state.OutlineFilePath;
        Assert.NotNull(shownRoots);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.RenderAsync(
            Request(lsp: new FakeLspDocument(Other, [Method("Bar", 3)]), file: Other),
            EditorSupportUpdateReason.Content, _ => true, cts.Token));

        Assert.Same(shownRoots, state.OutlineRoots);   // 画面に出ているものと食い違わない
        Assert.Equal(shownFile, state.OutlineFilePath);
    }

    [Fact]
    public async Task 案内へ落ちたらアウトライン状態も一緒に消える()
    {
        // 構造が画面から消えたのに状態だけ残ると、消えたツリーを相手にキャレット追従が空回りする。
        var host = new FakeHost { Diagnosis = Notice("marksman が見つかりません") };
        var (flow, state) = Flow(host);
        var tab = new EditorTab(Guid.NewGuid());
        await Render(flow, Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)]), source: tab));
        Assert.NotNull(state.OutlineRoots);

        await Render(flow, Request(lsp: null, source: tab));   // サーバーが落ちた＝案内へ

        Assert.Null(state.OutlineRoots);
        Assert.Null(state.OutlineFilePath);
    }

    [Fact]
    public async Task 同じタブが別ファイルを開いたら前の構造で呼び出しパネルを取りに行かない()
    {
        // 持ち主の判定がタブだけだと、開き直した直後のキャレット移動で「前のファイルの構造」を
        // 相手に②パネルだけ差し替えてしまう（画面は新しいファイル、中身は前のファイル）。
        var (flow, state) = Flow(new FakeHost());
        var tab = new EditorTab(Guid.NewGuid());
        await Render(flow, Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)]), source: tab));

        var frames = await Render(
            flow,
            Request(lsp: new FakeLspDocument(Other, [Method("Bar", 3)]), file: Other, source: tab),
            EditorSupportUpdateReason.Caret);

        // ②の差し替えではなく、新しいファイルの構造として組み直される。
        var outline = Assert.IsType<EditorSupportFrameContent.OutlineContent>(frames[0].Content);
        Assert.Equal(Other, outline.FilePath);
        Assert.Equal(Other, state.OutlineFilePath);
    }

    [Fact]
    public async Task 画面に出せなかったフレームはアウトライン状態も確定しない()
    {
        // 適用側が捨てたフレーム（WebView2 のブラウザプロセスが組み立て中に落ちた等）の状態まで書くと、
        // 画面は前のファイルのまま・状態だけ次のファイル、という食い違いが残る。
        var (flow, state) = Flow(new FakeHost());
        await Render(flow, Request(lsp: new FakeLspDocument(File, [Method("Foo", 10)])));
        var shownRoots = state.OutlineRoots;
        Assert.NotNull(shownRoots);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await flow.RenderAsync(
            Request(file: @"C:\work\app\README.md", lsp: null),
            EditorSupportUpdateReason.Content,
            _ => false,                      // 載せる先が無い＝画面は据え置き
            cts.Token);

        Assert.Same(shownRoots, state.OutlineRoots);
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
            EditorSupportUpdateReason.Content, Collect(frames), cts.Token));

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
        ILspDocument? lsp, string file = File, int caretLine = 10, EditorTab? source = null)
        => new(source ?? new EditorTab(Guid.NewGuid()), file, "class Foo { void Foo() {} }",
            caretLine, 0, lsp, "dark");

    private static async Task<List<EditorSupportFrame>> Render(
        EditorSupportRenderFlow flow,
        EditorSupportRenderRequest request,
        EditorSupportUpdateReason reason = EditorSupportUpdateReason.Content,
        TimeSpan? timeoutSafety = null)
    {
        var frames = new List<EditorSupportFrame>();
        using var cts = new CancellationTokenSource(timeoutSafety ?? TimeSpan.FromSeconds(20));
        await flow.RenderAsync(request, reason, Collect(frames), cts.Token);
        return frames;
    }

    /// <summary>フレームを受け取って「画面に出せた」と答える適用（＝通常のホスト）。</summary>
    private static Func<EditorSupportFrame, bool> Collect(List<EditorSupportFrame> frames)
        => frame => { frames.Add(frame); return true; };

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

        /// <summary>WebView2 を用意できるか（false＝用意できない）。</summary>
        public bool WebViewAvailable { get; init; } = true;

        public Task<bool> EnsureWebViewAsync()
        {
            Calls.Add("EnsureWebView");
            return Task.FromResult(WebViewAvailable);
        }

        public Task<string?> PreparePageAsync(string html, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public void ClearFullPageRequest() => Calls.Add("ClearFullPageRequest");
        public LspNoticeModel.Notice? DiagnoseLsp(string filePath) => Diagnosis;
        public void ScheduleLspReadyRetry() => ReadyRetryScheduled = true;
        public void StopLspReadyRetry() => ReadyRetryStopped = true;
    }
}
