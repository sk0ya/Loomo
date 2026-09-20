namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 1回分の描画に必要な入力。<b>UI から読む値はここで全部スナップショットする</b>——
/// 追従元タブの本文とキャレットは <c>await</c> をまたぐ間に動くので、読む時点を1か所に固定する。
/// </summary>
/// <param name="Source">追従元タブ。<b>同一性の目印としてしか使わない</b>（アウトラインの持ち主判定）。
/// 本文・キャレットは呼び元が読んで下の値へ入れること。</param>
internal sealed record EditorSupportRenderRequest(
    EditorTab Source,
    string? FilePath,
    string Text,
    int CaretLine,
    int CaretColumn,
    ILspDocument? Lsp,
    string PreviewTheme);

/// <summary>
/// 描画の途中でホスト（<see cref="ShellWindow"/>）へ返す問い合わせ。ここに出てくるのは
/// <b>WPF・WebView2・タイマーを触るもの</b>だけで、描画の筋道そのものは
/// <see cref="EditorSupportRenderFlow"/> に閉じている。
/// </summary>
internal interface IEditorSupportRenderHost
{
    /// <summary>WebView2 を用意する。<b>用意できたかを返す</b>——用意できないまま組み立てを続けると、
    /// ヘッダーとタイトルだけ新しいファイルへ変わって中身が前のまま、という半端な画面が残る。</summary>
    Task<bool> EnsureWebViewAsync();

    /// <summary>生成HTMLを適用前に一時ページへ書き込む（UIスレッドをブロックしない）。</summary>
    Task<string?> PreparePageAsync(string html, CancellationToken ct);

    /// <summary>本文差し替えができるページの鍵（復帰要求中は null＝必ずページ全体を組む）。</summary>
    string? ReadyPageKey { get; }

    /// <summary>ページ全体の組み直し要求を下ろす。<b>フレームを適用する直前</b>に呼ばれる——
    /// これより前で下ろすと、中断された描画が復帰要求を食い潰して固まったままになる。</summary>
    void ClearFullPageRequest();

    /// <summary>言語サーバーが使えない理由の見立て（未導入・未設定・起動失敗）。
    /// 理由を特定できたときだけ案内を返す。null＝理由不明（＝接続待ち）。</summary>
    LspNoticeModel.Notice? DiagnoseLsp(string filePath);

    /// <summary>言語サーバーの準備待ちポーリングを開始／停止する。</summary>
    void ScheduleLspReadyRetry();
    void StopLspReadyRetry();
}

/// <summary>
/// EditorSupport の描画本体——「入力を受け取り、<see cref="EditorSupportFrame"/> を出す」ところまで。
///
/// <para>
/// <b>なぜ <see cref="ShellWindow"/> の partial から抜いたか。</b>ペインが固まる／固まらないを決めている
/// 分岐（言語サーバーが未準備・無応答・構造が空・コールドスタート・本文差し替えの可否）は全部ここにあるのに、
/// partial に埋まっている限りテストからは1本も触れなかった。抜けなかったのは描画の<b>両端</b>——
/// タブから本文とキャレットを読むところと、UI へ書くところ——が WPF だからで、その両端だけを
/// <see cref="EditorSupportRenderRequest"/>（入力のスナップショット）と <c>apply</c>（フレームの適用）に
/// 押し出せば、真ん中は素の非同期処理として残る。
/// </para>
/// <para>
/// <c>apply</c> は<b>同期のコールバック</b>で、<c>ct</c> を確認した直後に呼び、<b>画面に出せたかを返す</b>
/// （出せなかったフレームの状態は確定しない＝画面と状態が食い違わない）。<c>IAsyncEnumerable</c> に
/// しなかったのはこのためで、yield と適用の間に await が挟まると「確認したときは有効だったフレームを
/// 追い越されてから適用する」余地が生まれる（＝中途半端な表示が残る、§26.5 で潰した形）。
/// </para>
/// </summary>
internal sealed class EditorSupportRenderFlow
{
    /// <summary>構造が空のときに取り直す回数（サーバーが解析途中のことがある）。</summary>
    private const int ColdStructureRetries = 6;
    private static readonly TimeSpan ColdStructureRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>「接続待ち」の案内を出すまでの猶予（準備待ちポーリングの tick 数）。</summary>
    internal const int ConnectingNoticeGraceTicks = 8;

    private readonly EditorSupportResolver _resolver;
    private readonly EditorSupportController _state;
    private readonly IWorkspaceService _workspace;
    private readonly ILspWorkspace _lspWorkspace;
    private readonly CodeEditorSupport _codeSupport;
    private readonly IEditorSupportRenderHost _host;

    public EditorSupportRenderFlow(
        EditorSupportResolver resolver,
        EditorSupportController state,
        IWorkspaceService workspace,
        ILspWorkspace lspWorkspace,
        CodeEditorSupport codeSupport,
        IEditorSupportRenderHost host)
    {
        _resolver = resolver;
        _state = state;
        _workspace = workspace;
        _lspWorkspace = lspWorkspace;
        _codeSupport = codeSupport;
        _host = host;
    }

    /// <summary>1回分の描画。<paramref name="apply"/> は組み上がったフレームを同期で適用し、
    /// <b>画面に出せたか</b>を返す（false＝載せる先が無い等で捨てた）。</summary>
    public async Task RenderAsync(
        EditorSupportRenderRequest request,
        EditorSupportUpdateReason reason,
        Func<EditorSupportFrame, bool> apply,
        CancellationToken ct)
    {
        var selection = _resolver.Resolve(request.FilePath);
        // キャレット移動はコード表示の呼び出しパネルだけを更新する。
        // Markdown/JSON/XML 等の提供者はキャレットを表示内容に使わないため、ここで止めないと
        // エディタでカーソルを動かすたびに本文全体の変換と WebView2 への setBody が走ってしまう。
        // Content と合流した要求（Content | Caret）は本文も古くなっているので通常どおり描画する。
        if (reason == EditorSupportUpdateReason.Caret
            && selection.Kind != EditorSupportKind.Code)
            return;

        if (selection.Kind == EditorSupportKind.Code && request.FilePath is not null)
        {
            // キャレット移動だけなら②パネルの差し替えで足りる（構造ツリーは作り直さない＝折りたたみを保つ）。
            if (reason == EditorSupportUpdateReason.Caret
                && _state.OutlineMatches(request.Source, request.FilePath))
            {
                await RefreshCallPanelsAsync(request, request.FilePath, apply, ct);
                return;
            }
            await RenderCodeAsync(request, request.FilePath, apply, ct);
            return;
        }
        await RenderProviderAsync(request, selection.Provider, apply, ct);
    }

    private async Task RenderProviderAsync(
        EditorSupportRenderRequest request,
        IEditorSupportProvider? provider,
        Func<EditorSupportFrame, bool> apply,
        CancellationToken ct)
    {
        // WebView2 を使う提供者のときだけ、<b>組み立てより先に</b>用意する。以前は pending を積んでから
        // Ensure を await していたので、その await で追い越されると「差し替えた pending が誰にも
        // 描かれない」状態が残った。ビジュアル提供者（CSV グリッド・画像・Hex）では WebView2 を作らない。
        string? readyPageKey = null;
        if (provider is not IEditorSupportVisualProvider)
        {
            var ready = await _host.EnsureWebViewAsync();
            ct.ThrowIfCancellationRequested();
            // 載せる先が無いなら<b>何も出さない</b>（画面は据え置き）。ここで組み立てだけ進めると、
            // 適用側で中身の差し替えに失敗してもヘッダーは書き換わってしまう。やり直しは
            // EditorSupportWebViewController の再生成タイマー（ReloadRequested）が持っている。
            if (!ready)
                return;
            readyPageKey = _host.ReadyPageKey;
        }
        var content = await _state.Pipeline.PrepareAsync(provider, EditorSupportContext.For(
            _workspace, request.FilePath, request.Text, readyPageKey, request.PreviewTheme));
        ct.ThrowIfCancellationRequested();

        EditorSupportFrameContent body;
        if (content.VisualProvider is { } visualProvider && request.FilePath is not null)
        {
            // 重い読み込み・パースは表示インスタンス側で済ませてから載せる（載せてから await しない）。
            var (visual, applyVisual) = await _state.PrepareVisualAsync(
                visualProvider, request.FilePath, request.Text, ct);
            ct.ThrowIfCancellationRequested();
            body = new EditorSupportFrameContent.VisualContent(visual, applyVisual);
        }
        else
        {
            string? preparedPageUrl = null;
            if (content.Html is { } html)
                preparedPageUrl = await _host.PreparePageAsync(html, ct);
            ct.ThrowIfCancellationRequested();
            body = new EditorSupportFrameContent.WebContent(
                content.Html, content.Body, content.Uri, content.MapFolder, content.PageKey,
                content.ShowEdit ? request.Text : null, preparedPageUrl);
        }
        _host.ClearFullPageRequest();
        Emit(apply, new EditorSupportFrame(
            content.Title, content.ShowSlide, content.ShowOutline,
            content.ShowEdit, content.ShowOpenInBrowser, content.ShowExport, body), ct);
    }

    private async Task RenderCodeAsync(
        EditorSupportRenderRequest request,
        string filePath,
        Func<EditorSupportFrame, bool> apply,
        CancellationToken ct)
    {
        var lsp = request.Lsp;
        var ready = lsp is not null && lsp.IsReady && CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath);
        var title = _codeSupport.DescribeTitle(filePath);
        if (CodeSupportDiag.IsEnabled)
        {
            _state.DiagnosticStopwatch ??= Stopwatch.StartNew();
            CodeSupportDiag.Log($"enter file={Path.GetFileName(filePath)} ready={ready} " +
                $"lsp={(lsp is null ? "null" : "ok")} connected={lsp?.IsConnected} docReady={lsp?.IsReady} " +
                $"match={(lsp is not null && CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath))} " +
                $"elapsed={_state.DiagnosticStopwatch?.ElapsedMilliseconds ?? 0}ms retryTick={_state.ReadyAttempts}");
        }
        if (!ready)
        {
            // ここで状態を消さない。案内を出す（＝画面から構造が消える）フレームが NoticeContent＝Clear を
            // 運ぶので、状態が消えるのは画面と同時になる。案内を出さない猶予の間は画面に前の構造が
            // 残っているのだから、状態も残っているのが正しい。別ファイルの構造が残っていても
            // OutlineMatches が弾くので、キャレット追従が誤爆することはない。
            // 未導入・未設定でないのに繋がらない＝起動／初期化に失敗している可能性がある。
            // 失敗は待っても解消しないので、猶予（ConnectingNoticeGraceTicks）を待たずに理由を出す。
            var diagnosed = _host.DiagnoseLsp(filePath);
            if (diagnosed is not null || _state.ReadyAttempts >= ConnectingNoticeGraceTicks)
                Emit(apply, CodeFrame(title, new EditorSupportFrameContent.NoticeContent(
                    diagnosed ?? LspNoticeModel.Build(null))), ct);
            _host.ScheduleLspReadyRetry();
            return;
        }
        _host.StopLspReadyRetry();
        CodeSupportDiag.Log($"ready reached after {_state.DiagnosticStopwatch?.ElapsedMilliseconds ?? 0}ms");
        var text = request.Text;
        var symbolsSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;
        var symbols = await CodeEditorSupportAnalysis.RequestDocumentSymbolsSafeAsync(lsp!, ct);
        CodeSupportDiag.Log($"documentSymbols {symbolsSw?.ElapsedMilliseconds ?? 0}ms count={symbols.Symbols.Count} timedOut={symbols.TimedOut}");
        ct.ThrowIfCancellationRequested();
        // 無応答は「シンボルが無い」ではない。理由を出して打ち切る（黙って待ち続けない・空で誤魔化さない）。
        if (symbols.TimedOut && symbols.Symbols.Count == 0)
        {
            Emit(apply, CodeFrame(title, new EditorSupportFrameContent.NoticeContent(
                LspNoticeModel.BuildTimeout(
                    Path.GetExtension(filePath), CodeEditorSupportAnalysis.RequestTimeout))), ct);
            LogOutlineShown("timeout");
            return;
        }
        var roots = CodeEditorSupport.ToOutline(symbols.Symbols, CodeEditorSupportAnalysis.SplitLines(text));
        if (roots.Count > 0)
        {
            // 構造だけ先に出す（②呼び出し解析は待たない）。SymbolRange は未取得なので null
            // （この後の PanelsContent が埋める）。
            Emit(apply, CodeFrame(title, new EditorSupportFrameContent.OutlineContent(
                roots, CurrentMemberLine1(roots, request), CallPanels.Empty,
                request.Source, filePath, SymbolRange: null, Caret(request))), ct);
            LogOutlineShown("structure");
        }
        else
        {
            Emit(apply, CodeFrame(title,
                new EditorSupportFrameContent.NoticeContent(LspNoticeModel.Build(null))), ct);
        }
        var panelsSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;
        var (panels, symbolRange) = await CodeEditorSupportAnalysis.FetchCallPanelsAsync(
            _lspWorkspace, lsp!, request.CaretLine, request.CaretColumn, ct);
        CodeSupportDiag.Log($"callPanels {panelsSw?.ElapsedMilliseconds ?? 0}ms " +
            $"in={panels.Incoming.Count} out={panels.Outgoing.Count} refs={panels.References.Count}");
        if (roots.Count > 0)
        {
            // 構造ツリーは作り直さず current 付替え＋②差し替えだけ（折りたたみを保つ）。
            Emit(apply, CodeFrame(title, new EditorSupportFrameContent.PanelsContent(
                CurrentMemberLine1(roots, request), panels, symbolRange, Caret(request))), ct);
            LogOutlineShown("panels");
            return;
        }
        // コールドスタート：サーバーがまだ解析途中で構造が空のことがあるので、少し待って取り直す。
        // 無応答（期限切れ）になったら打ち切る——応答しないサーバーへ6回×8秒を投げても無駄に遅くなるだけ。
        for (var attempt = 0; attempt < ColdStructureRetries; attempt++)
        {
            symbols = await CodeEditorSupportAnalysis.RequestDocumentSymbolsSafeAsync(lsp!, ct);
            ct.ThrowIfCancellationRequested();
            if (symbols.TimedOut)
                break;
            roots = CodeEditorSupport.ToOutline(symbols.Symbols, CodeEditorSupportAnalysis.SplitLines(text));
            if (roots.Count > 0)
                break;
            await Task.Delay(ColdStructureRetryDelay, ct);
        }
        CodeSupportDiag.Log($"cold structure refetch count={roots.Count}");
        Emit(apply, CodeFrame(title, roots.Count > 0
            ? new EditorSupportFrameContent.OutlineContent(
                roots, CurrentMemberLine1(roots, request), panels,
                request.Source, filePath, symbolRange, Caret(request))
            : new EditorSupportFrameContent.NoticeContent(LspNoticeModel.Build(null))), ct);
        LogOutlineShown(roots.Count > 0 ? "cold-structure+panels" : "empty");
    }

    /// <summary>キャレット移動に伴う②呼び出しパネルだけの差し替え。current は CaretMoved 時点で即時更新済み。</summary>
    private async Task RefreshCallPanelsAsync(
        EditorSupportRenderRequest request,
        string filePath,
        Func<EditorSupportFrame, bool> apply,
        CancellationToken ct)
    {
        if (!_state.ShouldRefreshCallPanels(
                request.Source, filePath, request.CaretLine, request.CaretColumn))
            return;
        var lsp = request.Lsp;
        if (lsp is null || !lsp.IsReady || !CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath))
            return;
        var (panels, symbolRange) = await CodeEditorSupportAnalysis.FetchCallPanelsAsync(
            _lspWorkspace, lsp, request.CaretLine, request.CaretColumn, ct);
        Emit(apply, CodeFrame(_codeSupport.DescribeTitle(filePath),
            new EditorSupportFrameContent.PanelsContent(
                CurrentLine1: null, panels, symbolRange, Caret(request))), ct);
    }

    /// <summary>
    /// フレームを1枚出す。<b>描画が UI と状態へ触れる唯一の出口</b>で、順序は
    /// 「キャンセル確認 → アウトライン状態の確定 → 適用」に固定してある。
    /// <para>
    /// 状態の確定をここへ集めたのが要点。以前は <c>_state.SetOutline</c> 等を描画の途中で書いていて、
    /// <c>ct</c> の確認より<b>前</b>に書く箇所があった（コールドスタート経路）。追い越されて捨てられた
    /// 描画が「画面に出ていない構造」を記録すると、画面にはツリーが出ているのに
    /// <see cref="EditorSupportController.ShouldRefreshCallPanels"/> が常に false を返し、
    /// <b>②パネルだけが二度と更新されない</b>——ペインが固まったようにしか見えない壊れ方になる。
    /// </para>
    /// </summary>
    private void Emit(Func<EditorSupportFrame, bool> apply, EditorSupportFrame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 出せたときだけ状態を確定する。適用側が捨てたフレームの状態まで書くと、画面は前のまま
        // 状態だけ次のファイルのものになり、そこから先はキャレット追従が食い違い続ける。
        if (apply(frame))
            _state.CommitOutline(frame.Content.Outline);
    }

    private static (int Line, int Col) Caret(EditorSupportRenderRequest request)
        => (request.CaretLine, request.CaretColumn);

    private static EditorSupportFrame CodeFrame(string title, EditorSupportFrameContent content)
        => new(title, ShowSlide: false, ShowOutline: false,
               ShowEdit: false,
               ShowOpenInBrowser: false, ShowExport: false, content);

    private static int CurrentMemberLine1(
        IReadOnlyList<OutlineNode> roots, EditorSupportRenderRequest request)
        => CodeEditorSupportAnalysis.CurrentMemberLine1(roots, request.CaretLine, request.CaretColumn);

    private void LogOutlineShown(string phase)
    {
        if (_state.DiagnosticStopwatch is not null)
            CodeSupportDiag.Log($"shown[{phase}], TOTAL {_state.DiagnosticStopwatch.ElapsedMilliseconds}ms");
    }
}
