namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: EditorSupport ペイン（Markdown プレビュー等の表示・スクロール同期）。
/// 自動表示はしない（明示操作で開いたときだけアクティブエディタに追従して描く）。
/// <para>
/// 更新の入口は <see cref="InvalidateEditorSupport"/> ただ一つで、実際の描画は
/// <see cref="EditorSupportUpdateLoop"/> が同時1本に直列化する。描画は
/// <see cref="EditorSupportFrame"/> を組み立て、<see cref="EditorSupportFramePresenter.Apply"/> が
/// 同期で丸ごと適用する——<b>await をまたいで UI をバラバラに書かない</b>のがこのファイルの規約。
/// （以前はタイトル・ヘッダー・ビジュアル・WebView を別々に書いていたため、追い越された描画が
/// 中途半端な状態を残して「固まったように見える」ことがあった。）
/// </para>
/// <para>
/// <b>「見えているのに中身が古いまま」を構造で潰してある。</b>描けない状態（ペインが閉じている・
/// 舞台に立っていない）で来た要求は捨てずに持ち越されるが、それを描かせるのに
/// <see cref="InvalidateEditorSupport"/> の呼び出しを<b>当てにしない</b>——可視化の経路
/// （タイル表示の開閉・舞台の差し替え・俯瞰の開閉・袖からの復帰…）は増え続けるので、
/// 呼び忘れが1つあるだけで固まる。網は二重にしてある：
/// <see cref="SyncEditorSupportRenderability"/> をレイアウト組み直しの合流点
/// （<c>RebuildPaneLayout</c> / <c>RebuildStage</c>）から呼んで<b>即座に</b>拾い、
/// それでも漏れた経路は <see cref="EditorSupportUpdateLoop"/> の見回りが拾う。
/// </para>
/// <para>
/// <b>このファイルに残っているのは描画の両端だけ</b>——追従元タブから本文・キャレットを読む
/// （<see cref="RenderEditorSupportAsync"/>）ところと、UI へ書く（<see cref="EditorSupportFramePresenter.Apply"/>）
/// ところ。真ん中の筋道（言語サーバーが未準備・無応答・構造が空・コールドスタート、本文差し替えの可否）は
/// <see cref="EditorSupportRenderEngine"/> にあり、そこは partial の外なのでテストから叩ける。
/// このファイルは <see cref="IEditorSupportRenderHost"/> として、WPF・WebView2・タイマーを触る部分だけを返す。
/// </para>
/// </summary>
public partial class ShellWindow : IEditorSupportRenderHost {
    private EditorSupportUpdateLoop? _editorSupportLoop;
    private EditorSupportFramePresenter? _editorSupportFramePresenter;
    private EditorSupportOutlinePresenter? _editorSupportOutlinePresenter;
    private MouseNavigationController? _editorSupportMouseNavigation;
    private EditorSupportFramePresenter EditorSupportFramePresenter
        => _editorSupportFramePresenter ??= new EditorSupportFramePresenter(
            _editorSupport, _editorSupport.WebView, EditorSupportContentHost,
            EditorSupportSettingsButton, EditorSupportSlideToggle, EditorSupportOutlineToggle,
            EditorSupportEditButton, EditorSupportOpenInBrowserButton, EditorSupportExportButton,
            EditorSupportTitle,
            () => _settings.Appearance.MarkdownSlideMode,
            () => _settings.Appearance.MarkdownOutlineVisible,
            EnsureCodeOutlineView, ShowEditorSupportVisual, HideEditorSupportVisual,
            () => { _editorSupportForceFullPage = true; InvalidateEditorSupport(); },
            () => _ = CaptureWebThumbnailAsync(PaneKind.EditorSupport));
    private EditorSupportOutlinePresenter EditorSupportOutlinePresenter
        => _editorSupportOutlinePresenter ??= new EditorSupportOutlinePresenter(
            _editorSupport, FocusEditorSupportSource, OpenPathInEditorAsync,
            InstallLspForEditorSupportSource,
            () => _vm.LspPrompt.OpenSettingsCommand.Execute(null),
            url => { _ = OpenUrlInBrowserAsync(url, null); });
    private MouseNavigationController EditorSupportMouseNavigation
        => _editorSupportMouseNavigation ??= new MouseNavigationController(
            _paneElements, BrowserNavigateHistory,
            (source, back) => PaneContent<FilesPaneView>(FilesPaneHost).NavigateHistory(source, back),
            back => { _ = EditorSupportNavigateHistoryAsync(back); });
    /// <summary>次の描画でページ全体を組み直す（本文差し替えを使わない）。ナビゲーション失敗・
    /// 応答なし・差し替え先ページ喪失からの復帰で立てる一度きりのフラグ。</summary>
    private bool _editorSupportForceFullPage;
    private EditorSupportUpdateLoop EditorSupportLoop => _editorSupportLoop ??= new EditorSupportUpdateLoop(
        CanRenderEditorSupport, RenderEditorSupportAsync, new DispatcherRenderabilityWatch(),
        ex => CodeSupportDiag.Log($"render failed: {ex}"));

    /// <summary>
    /// ペインの見え方が変わったので、持ち越してある要求を拾い直す。<b>要求が無ければ何もしない</b>ので、
    /// レイアウトの組み直し（タイル・舞台・袖・俯瞰）から無条件に呼べる。
    /// <para>
    /// これと <see cref="EditorSupportUpdateLoop"/> の見回りは<b>二重の網</b>で、狙いが違う。
    /// こちらは可視化した瞬間に描くための早道、見回りは「ここを呼び忘れた経路」でも必ず拾うための保険。
    /// 「可視化した側が <see cref="InvalidateEditorSupport"/> を呼ぶ」に頼っていた頃は、呼び忘れた経路の分だけ
    /// 「ペインは見えているのに中身が前のファイルのまま」が残っていた。
    /// </para>
    /// </summary>
    private void SyncEditorSupportRenderability() => _editorSupportLoop?.PollRenderability();
    /// <summary>EditorSupport の再描画を要求する唯一の入口。描けない状態なら要求は保持され、
    /// 可視化された時点で必ず1回描かれる（取りこぼさない）。</summary>
    private void InvalidateEditorSupport(
        EditorSupportUpdateReason reason = EditorSupportUpdateReason.Content) {
        UpdateEditorSupportFileWatch();   // 追従元・提供者が変わりうる合図でもある（§24.8）
        EditorSupportLoop.Invalidate(reason);
    }
    /// <summary>いま中身を描いてよいか。可視表現の境界判定は <see cref="EditorSupportRenderPolicy"/> に一元化してある。</summary>
    private bool CanRenderEditorSupport()
        => _editorSupport.Source is not null
           && EditorSupportRenderPolicy.ShouldRender(
               _stageActive && _stagePane == PaneKind.EditorSupport,
               _dockActive ? IsDockPaneShown(PaneKind.EditorSupport) : IsPaneVisible(PaneKind.EditorSupport),
               IsEditorSupportInThumbnail());
    private void OpenEditorSupport(EditorTab sourceTab) {
        SwitchEditorSupportSource(sourceTab, force: true);
        if (_stageActive)
            SetStagePane(PaneKind.EditorSupport);   // ソロは舞台へ立てる
        else if (_dockActive && _dockMode.IsDocked(PaneKind.EditorSupport))
            EnsureDockPaneShown(PaneKind.EditorSupport);   // ドックはその領域へ出す
        else
            ShowEditorSupportPane();                 // タイルは Editor の右隣へ開く
        InvalidateEditorSupport();
        RecordTrailPreview(sourceTab);
    }
    private async void OnEditorSupportBack(object sender, RoutedEventArgs e) => await EditorSupportGoBackAsync();
    /// <summary>マウスの戻る/進むボタン。ウィンドウ全体で受けるので、ポインタ下のペインを見て配る
    /// （判定は <see cref="MouseNavigationPolicy"/>）。</summary>
    private void OnShellPreviewMouseNavigate(object sender, MouseButtonEventArgs e)
        => EditorSupportMouseNavigation.Handle(e);
    private Task EditorSupportGoBackAsync() => EditorSupportNavigateHistoryAsync(back: true);
    private async Task EditorSupportNavigateHistoryAsync(bool back) {
        await _editorSupport.NavigateHistoryAsync(back, _editorTabs, tab => ActivateEditorTab(tab.Id), path => OpenFileInNewEditorTabAsync(path));
        UpdateEditorSupportNavAffordances();
    }
    private void UpdateEditorSupportNavAffordances() {
        if (EditorSupportBackButton is not null)
            EditorSupportBackButton.IsEnabled = _editorSupport.History.CanGoBack;
    }
    private void SwitchEditorSupportSource(EditorTab sourceTab, bool force = false) {
        if (!_editorSupport.TryChangeSource(
                sourceTab, force, EditorSupportSource_ViewportScrolled, EditorSupportSource_CaretMoved))
            return;
        UpdateEditorSupportNavAffordances();
        UpdateEditorSupportPinToggle();
        InvalidateEditorSupport();
    }
    private void OnToggleEditorSupportPin(object sender, RoutedEventArgs e) {
        var source = _editorSupport.ApplyPinState(
            EditorSupportPinToggle.IsChecked == true, _activeEditorTab);
        UpdateEditorSupportPinToggle();
        if (source is not null)
            SwitchEditorSupportSource(source, force: true);
    }
    private void OnToggleEditorSupportSlideMode(object sender, RoutedEventArgs e) {
        _settings.Appearance.MarkdownSlideMode = EditorSupportSlideToggle.IsChecked == true;
        ApplyEditorSupportPreviewToggle();
    }
    /// <summary>アウトライン（見出し一覧）の表示切替。ページ構造が変わるので描き直す
    /// （鍵に入っているので本文差し替えではなくフル再構築になる）。</summary>
    private void OnToggleEditorSupportOutline(object sender, RoutedEventArgs e) {
        _settings.Appearance.MarkdownOutlineVisible = EditorSupportOutlineToggle.IsChecked == true;
        ApplyEditorSupportPreviewToggle();
    }
    /// <summary>ヘッダーのプレビュー表示トグル（アウトライン・発表モード）を反映する共通処理。
    /// <b>保存まで含める</b>のがここの要点——設定を書き換えるだけでは共有シングルトンの中にしか残らず、
    /// 次に誰かが別の外観設定を保存したときについでに書かれる（＝再起動で戻るか残るかが運任せになる）。
    /// 切り離した複製ウィンドウも同じ設定で描いているので一緒に描き直す（本体と食い違わせない）。</summary>
    private void ApplyEditorSupportPreviewToggle() {
        _vm.Appearance.SaveOutsideOverlay();
        InvalidateEditorSupport();
        foreach (var mirror in Detached.AllItems.Select(i => i.Content).OfType<DetachedEditorSupportView>())
            mirror.Refresh();
    }
    private async void OnOpenEditorSupportInBrowser(object sender, RoutedEventArgs e)
        => await EditorSupportBrowserExportCoordinator.OpenAsync(
            _editorSupport.Source, _editorSupportResolver, _editorSupport.Pipeline,
            _workspace, _settings.Appearance.MarkdownPreviewTheme,
            OpenUrlInBrowserAsync, OpenEditorSupportSnapshotInBrowserAsync);
    private void OnEditorSupportSettingsClick(object sender, RoutedEventArgs e)
        => _editorSupport.CurrentSettingsVisual?.OpenSettings();
    private void UpdateEditorSupportPinToggle() {
        EditorSupportPinToggle.IsChecked = _editorSupport.IsPinned;
        EditorSupportPinToggle.ToolTip = _editorSupport.IsPinned
            ? "ピン留めを解除してアクティブなエディタに追従"
            : "現在のサポート対象にピン留め";
    }
    private void ScheduleEditorSupportUpdate() {
        if (_editorSupport.Source is null)
            return;
        if (_editorSupportDebounceTimer is null) {
            _editorSupportDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _editorSupportDebounceTimer.Tick += (s, _) => {
                ((DispatcherTimer)s!).Stop();
                InvalidateEditorSupport();
            };
        }
        _editorSupportDebounceTimer.Stop();
        _editorSupportDebounceTimer.Start();
    }

    // ===== 描画：フレームを1個組み立てて一括適用する =====

    /// <summary>
    /// 1回分の描画。<see cref="EditorSupportUpdateLoop"/> からのみ呼ばれる（同時に1本だけ）。
    /// <b>ここがやるのは両端だけ</b>——タブから本文・キャレットを読んで
    /// <see cref="EditorSupportRenderRequest"/> に固め、出てきたフレームを
    /// <see cref="EditorSupportFramePresenter.Apply"/> で適用する。描画の筋道は
    /// <see cref="EditorSupportRenderEngine"/>（テスト可能）にある。
    /// </summary>
    private async Task RenderEditorSupportAsync(EditorSupportUpdateReason reason, CancellationToken ct) {
        var source = _editorSupport.Source;
        if (source is null)
            return;
        // 本文とキャレットは await をまたぐ間に動くので、読む時点をここ1か所に固定する。
        var caret = source.Control.Caret;
        var request = new EditorSupportRenderRequest(
            source, source.Control.FilePath, source.Control.Text, caret.Line, caret.Column,
            GetLspDocument(source), _settings.Appearance.MarkdownPreviewTheme);
        await EditorSupportEngine.RenderAsync(request, reason, EditorSupportFramePresenter.Apply, ct);
    }
    private CodeOutlineView EnsureCodeOutlineView() => EditorSupportOutlinePresenter.EnsureView();

    // ===== 描画本体へ渡す、WPF 側でしかできない部分 =====

    private EditorSupportRenderEngine? _editorSupportEngine;
    private EditorSupportRenderEngine EditorSupportEngine => _editorSupportEngine ??= new EditorSupportRenderEngine(
        _editorSupportResolver, _editorSupport, _workspace, _lspWorkspace, _codeSupport, this);

    async Task<bool> IEditorSupportRenderHost.EnsureWebViewAsync()
        => await EnsureEditorSupportViewAsync() is not null && _editorSupport.WebView.Core is not null;

    Task<string?> IEditorSupportRenderHost.PreparePageAsync(string html, CancellationToken ct)
        => _editorSupport.WebView.PreparePageAsync(html, ct);

    /// <summary>復帰要求が出ていれば本文差し替えを使わせない（必ず html が組み上がる＝1回で収束する）。</summary>
    string? IEditorSupportRenderHost.ReadyPageKey
        => _editorSupportForceFullPage ? null : _editorSupport.WebView.ReadyPageKey;

    void IEditorSupportRenderHost.ClearFullPageRequest() => _editorSupportForceFullPage = false;

    LspNoticeModel.Notice? IEditorSupportRenderHost.DiagnoseLsp(string filePath) {
        var prompt = _editorLspNotices.EvaluatePrompt(filePath);
        var failure = prompt is null ? _editorLspNotices.EvaluateFailure(filePath) : null;
        return prompt is null && failure is null ? null : LspNoticeModel.Build(prompt, failure);
    }

    void IEditorSupportRenderHost.ScheduleLspReadyRetry() => ScheduleCodeReadyRetry();
    void IEditorSupportRenderHost.StopLspReadyRetry() => StopCodeReadyRetry();

    private void InstallLspForEditorSupportSource()
    {
        var filePath = _editorSupport.Source?.Control.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return;
        var info = _editorLspNotices.EvaluatePrompt(filePath);
        if (info is null)
            return; // 既に導入済み等（案内ボタンが古い）：何もしない
        _lspManagement.InstallForPrompt(info);
    }
    private static readonly TimeSpan CodeReadyRetryInterval = TimeSpan.FromMilliseconds(200);
    private const int CodeReadyMaxRetries = 125;
    private void ScheduleCodeReadyRetry()
        => _editorSupport.ScheduleReadyRetry(CodeReadyRetryInterval, CodeReadyRetry_Tick);
    private void StopCodeReadyRetry()
        => _editorSupport.StopReadyRetry();
    /// <summary>言語サーバーの準備待ちポーリング。<b>自分では描かず</b>ループへ要求を投げるだけなので、
    /// 描画の入れ子（tick が await 中に次の tick が走って解析要求が積み上がる）が起きない。
    /// 判断そのものは <see cref="LspReadyRetryPolicy"/>（純関数・テスト可能）にある——ここを閉じると
    /// そのタブでは二度と構造が出ないので、止める条件はテストで固定しておきたい。</summary>
    private void CodeReadyRetry_Tick(object? sender, EventArgs e) {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        var lsp = source is null ? null : GetLspDocument(source);
        var step = LspReadyRetryPolicy.Next(
            codeSourceOpen: filePath is not null && _codeSupport.CanHandle(filePath),
            serverReady: lsp is not null && lsp.IsReady && filePath is not null
                         && CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath),
            attempts: _editorSupport.AdvanceReadyAttempt(),
            maxAttempts: CodeReadyMaxRetries,
            noticeGraceTicks: EditorSupportRenderEngine.ConnectingNoticeGraceTicks);
        switch (step) {
            case LspReadyRetryStep.Stop:
                StopCodeReadyRetry();   // 上限まで待った：案内のまま諦める（ペイン再オープンでやり直す）
                break;
            case LspReadyRetryStep.Render:
                InvalidateEditorSupport();
                break;
        }
    }
    private void ScheduleCodeCallPanelsRefresh()
        => _editorSupport.ScheduleCaretRefresh(() => InvalidateEditorSupport(EditorSupportUpdateReason.Caret));
    private void EditorSupportSource_CaretMoved(object? sender, CaretInfo e)
    {
        // current 表示はアウトラインの純ロジックだけで決まる。150ms のデバウンスや
        // references/callHierarchy の応答を待たず、キャレット移動と同時に付け替える。
        if (_editorSupport.CurrentOutlineLine(
                _editorSupport.Source, _editorSupport.Source?.Control.FilePath, e.Line, e.Column) is { } currentLine)
            _editorSupport.OutlineView?.SetCurrent(currentLine);
        ScheduleCodeCallPanelsRefresh();
    }
    private void ToggleMarkdownTaskCheckbox(int lineIndex) {
        var source = _editorSupport.Source;
        if (source is null)
            return;
        var text = MarkdownTaskCheckboxPolicy.ToggleLine(source.Control.Text, lineIndex);
        if (text is null)
            return;
        _syncingEditorFromSupport = true;
        try { source.Control.SetText(text); }
        finally { _syncingEditorFromSupport = false; }
        ScheduleEditorSupportUpdate();
    }
    private bool IsEditorSupportInThumbnail()
        => EditorSupportRenderPolicy.IsInThumbnail(
            _dockActive,
            IsSessionEnabled(PaneKind.EditorSupport),
            _stageActive,
            _overviewActive,
            _stagePane == PaneKind.EditorSupport,
            IsShownInMain(PaneKind.EditorSupport));
}
