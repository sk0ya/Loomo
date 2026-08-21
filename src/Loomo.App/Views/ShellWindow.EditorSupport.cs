namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: EditorSupport ペイン（Markdown プレビュー等の表示・スクロール同期）。
/// 自動表示はしない（明示操作で開いたときだけアクティブエディタに追従して描く）。
/// <para>
/// 更新の入口は <see cref="InvalidateEditorSupport"/> ただ一つで、実際の描画は
/// <see cref="EditorSupportUpdateLoop"/> が同時1本に直列化する。描画は
/// <see cref="EditorSupportFrame"/> を組み立て、<see cref="ApplyEditorSupportFrame"/> が
/// 同期で丸ごと適用する——<b>await をまたいで UI をバラバラに書かない</b>のがこのファイルの規約。
/// （以前はタイトル・ヘッダー・ビジュアル・WebView を別々に書いていたため、追い越された描画が
/// 中途半端な状態を残して「固まったように見える」ことがあった。）
/// </para>
/// <para>
/// <b>このファイルに残っているのは描画の両端だけ</b>——追従元タブから本文・キャレットを読む
/// （<see cref="RenderEditorSupportAsync"/>）ところと、UI へ書く（<see cref="ApplyEditorSupportFrame"/>）
/// ところ。真ん中の筋道（言語サーバーが未準備・無応答・構造が空・コールドスタート、本文差し替えの可否）は
/// <see cref="EditorSupportRenderFlow"/> にあり、そこは partial の外なのでテストから叩ける。
/// このファイルは <see cref="IEditorSupportRenderHost"/> として、WPF・WebView2・タイマーを触る部分だけを返す。
/// </para>
/// </summary>
public partial class ShellWindow : IEditorSupportRenderHost {
    private EditorSupportUpdateLoop? _editorSupportLoop;
    private bool _markdownEditMode;
    /// <summary>次の描画でページ全体を組み直す（本文差し替えを使わない）。ナビゲーション失敗・
    /// 応答なし・差し替え先ページ喪失からの復帰で立てる一度きりのフラグ。</summary>
    private bool _editorSupportForceFullPage;
    private EditorSupportUpdateLoop EditorSupportLoop => _editorSupportLoop ??= new EditorSupportUpdateLoop(
        CanRenderEditorSupport, RenderEditorSupportAsync,
        ex => CodeSupportDiag.Log($"render failed: {ex}"));
    /// <summary>EditorSupport の再描画を要求する唯一の入口。描けない状態なら要求は保持され、
    /// 可視化された時点で必ず1回描かれる（取りこぼさない）。</summary>
    private void InvalidateEditorSupport(
        EditorSupportUpdateReason reason = EditorSupportUpdateReason.Content)
        => EditorSupportLoop.Invalidate(reason);
    /// <summary>いま中身を描いてよいか。可視表現の境界判定は <see cref="EditorSupportRenderPolicy"/> に一元化してある。</summary>
    private bool CanRenderEditorSupport()
        => _editorSupport.Source is not null
           && EditorSupportRenderPolicy.ShouldRender(
               _stageActive && _stagePane == PaneKind.EditorSupport,
               IsPaneVisible(PaneKind.EditorSupport),
               IsEditorSupportInThumbnail());
    private void OpenEditorSupport(EditorTab sourceTab) {
        SwitchEditorSupportSource(sourceTab, force: true);
        if (_stageActive)
            SetStagePane(PaneKind.EditorSupport);   // ソロは舞台へ立てる
        else
            ShowEditorSupportPane();                 // タイルは Editor の右隣へ開く
        InvalidateEditorSupport();
        RecordTrailPreview(sourceTab);
    }
    private async void OnEditorSupportBack(object sender, RoutedEventArgs e) => await EditorSupportGoBackAsync();
    /// <summary>マウスの戻る/進むボタン。ウィンドウ全体で受けるので、ポインタ下のペインを見て配る
    /// （判定は <see cref="MouseNavigationPolicy"/>）。</summary>
    private void OnShellPreviewMouseNavigate(object sender, MouseButtonEventArgs e) {
        var pane = e.OriginalSource is DependencyObject source ? FindPaneOf(source) : null;
        if (MouseNavigationPolicy.Resolve(e.ChangedButton, pane) is not { } command)
            return;
        e.Handled = true;
        switch (command.Target) {
            case MouseNavigationTarget.Browser:
                BrowserNavigateHistory(command.Back);
                break;
            case MouseNavigationTarget.Files:
                FilesPaneHost.NavigateHistory(e.OriginalSource as DependencyObject, command.Back);
                break;
            default:
                _ = EditorSupportNavigateHistoryAsync(command.Back);
                break;
        }
    }
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
        if (!_editorSupport.TryChangeSource(sourceTab, force, out var previous))
            return;
        if (previous is not null) {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        StopCodeReadyRetry();
        _editorSupport.DiagnosticStopwatch = null;   // 追従元が変わったので計測もやり直し
        UpdateEditorSupportNavAffordances();
        sourceTab.Control.ViewportScrolled += EditorSupportSource_ViewportScrolled;
        sourceTab.Control.CaretMoved += EditorSupportSource_CaretMoved;
        UpdateEditorSupportPinToggle();
        InvalidateEditorSupport();
    }
    private void OnToggleEditorSupportPin(object sender, RoutedEventArgs e) {
        _editorSupport.IsPinned = EditorSupportPinToggle.IsChecked == true;
        UpdateEditorSupportPinToggle();
        if (_editorSupport.IsPinned) {
            if (_editorSupport.Source is null && _activeEditorTab is not null)
                SwitchEditorSupportSource(_activeEditorTab, force: true);
            return;
        }
        if (_activeEditorTab is not null)
            SwitchEditorSupportSource(_activeEditorTab, force: true);
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
    private async void OnOpenEditorSupportInBrowser(object sender, RoutedEventArgs e) {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;
        // 解決は描画と同じ Resolver を通す（Registry を直に引くと Hex/コードのフォールバックが抜け落ちる）。
        var provider = _editorSupportResolver.Resolve(filePath).Provider;
        var result = await _editorSupport.Pipeline.PrepareAsync(provider, EditorSupportContext.For(
            _workspace, filePath, source.Control.Text, null,
            _settings.Appearance.MarkdownPreviewTheme));
        if (result.Uri is { } uri)
        {
            await OpenUrlInBrowserAsync(uri, result.Title);
            return;
        }
        if (result.Html is not { } html)
            return; // ビジュアル提供者（CSV/TSV グリッド等）や対応の無いファイルは開ける HTML が無い。
        await OpenEditorSupportSnapshotInBrowserAsync(html, result.MapFolder, result.Title);
    }
    private void UpdateEditorSupportHeaderButtons(
        bool showSlide, bool showOutline, bool showEdit, bool showOpenInBrowser, bool showExport) {
        EditorSupportSlideToggle.Visibility = showSlide ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportOutlineToggle.Visibility = showOutline ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportEditButton.Visibility = showEdit ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportEditButton.IsChecked = showEdit && _markdownEditMode;
        EditorSupportEditButton.ToolTip = _markdownEditMode
            ? "プレビューに戻る"
            : "Markdownをこの画面で直接編集";
        EditorSupportOpenInBrowserButton.Visibility = showOpenInBrowser ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportExportButton.Visibility = showExport ? Visibility.Visible : Visibility.Collapsed;
        // 押した状態は設定が真実（アプリ再起動後も持ち越す）。トグルの見た目は毎フレームここで設定へ揃える
        // ——起動直後は設定が ON でもボタンだけ OFF に見える、という食い違いを起こさないため。
        EditorSupportSlideToggle.IsChecked = _settings.Appearance.MarkdownSlideMode;
        EditorSupportOutlineToggle.IsChecked = _settings.Appearance.MarkdownOutlineVisible;
    }

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
    /// <see cref="ApplyEditorSupportFrame"/> で適用する。描画の筋道は
    /// <see cref="EditorSupportRenderFlow"/>（テスト可能）にある。
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
        await EditorSupportFlow.RenderAsync(request, reason, ApplyEditorSupportFrame, ct);
    }
    /// <summary>組み上がったフレームを丸ごと適用する。<b>ここが UI を書く唯一の場所で、同期・途中 return なし。</b></summary>
    private void ApplyEditorSupportFrame(EditorSupportFrame frame) {
        // ブラウザプロセスが落ちた後の WebView2 は CoreWebView2 を読むだけで例外を投げるので、
        // 必ず均した参照（Core）を使う——ここで落とすと、以降このフレームは適用されずペインが空のまま残る。
        var core = _editorSupport.WebView.Core;
        EditorSupportSettingsButton.Visibility = Visibility.Collapsed;
        if (!frame.ShowEdit)
            _markdownEditMode = false;
        UpdateEditorSupportHeaderButtons(
            frame.ShowSlide, frame.ShowOutline, frame.ShowEdit,
            frame.ShowOpenInBrowser, frame.ShowExport);
        EditorSupportTitle.Text = frame.Title;
        switch (frame.Content) {
            case EditorSupportFrameContent.VisualContent visual:
                _editorSupport.MountVisual(EditorSupportContentHost, visual.Visual);
                EditorSupportSettingsButton.Visibility = visual.Visual is IEditorSupportSettingsVisual
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                visual.Apply();
                break;
            case EditorSupportFrameContent.OutlineContent outline:
                var outlineView = EnsureCodeOutlineView();
                ShowEditorSupportVisual(outlineView);
                outlineView.ShowOutline(outline.Roots, outline.CurrentLine1, outline.Panels);
                break;
            case EditorSupportFrameContent.PanelsContent panels:
                if (_editorSupport.OutlineView is not { } shown)
                    break;   // 構造がまだ出ていないなら②だけ差し替えても意味が無い
                if (panels.CurrentLine1 is int current)
                    shown.SetCurrentAndPanels(current, panels.Panels);
                else
                    shown.SetPanels(panels.Panels);
                break;
            case EditorSupportFrameContent.NoticeContent notice:
                var noticeView = EnsureCodeOutlineView();
                ShowEditorSupportVisual(noticeView);
                noticeView.ShowNotice(notice.Notice);
                break;
            case EditorSupportFrameContent.WebContent web:
                HideEditorSupportVisual();
                if (core is null) {
                    // WebView2 を用意できなかった＝ペインは空のまま。ここで自分へ投げ返すと
                    // 初期化失敗のたびに即再試行して回り続けるので、記録だけ残して次の要求を待つ。
                    CodeSupportDiag.Log("webview unavailable: frame dropped");
                    break;
                }
                if (_editorSupport.WebView.Show(core, web) == EditorSupportPageApplyResult.NeedsFullPage) {
                    // 差し替え先のページが（別ファイルへの遷移・読み込み失敗で）もう無い。
                    // 黙って捨てると古い表示のまま固まるので、ページ全体で組み直す。
                    _editorSupportForceFullPage = true;
                    InvalidateEditorSupport();
                    break;
                }
                _ = CaptureWebThumbnailAsync(PaneKind.EditorSupport);
                break;
        }
    }
    private CodeOutlineView EnsureCodeOutlineView() {
        if (_editorSupport.OutlineView is not null)
            return _editorSupport.OutlineView;
        var view = new CodeOutlineView();
        view.SourceLocationActivated += (_, e) =>
            FocusEditorSupportSource(e.Line1 > 0 ? e.Line1 : null, e.Column0, alignTop: true);
        view.FileLocationActivated += (_, e) =>
            _ = OpenPathInEditorAsync(e.Path, e.Line1, column: e.Column0 + 1, alignTop: true);
        view.InstallRequested += (_, _) => InstallLspForEditorSupportSource();
        view.OpenLspSettingsRequested += (_, _) => _vm.LspPrompt.OpenSettingsCommand.Execute(null);
        view.OpenDocsRequested += (_, url) => _ = OpenUrlInBrowserAsync(url, null);
        _editorSupport.OutlineView = view;
        return view;
    }

    // ===== 描画本体（EditorSupportRenderFlow）へ渡す、WPF 側でしかできない部分 =====

    private EditorSupportRenderFlow? _editorSupportFlow;
    private EditorSupportRenderFlow EditorSupportFlow => _editorSupportFlow ??= new EditorSupportRenderFlow(
        _editorSupportResolver, _editorSupport, _workspace, _lspWorkspace, _codeSupport, this);

    Task IEditorSupportRenderHost.EnsureWebViewAsync() => EnsureEditorSupportViewAsync();

    Task<string?> IEditorSupportRenderHost.PreparePageAsync(string html, CancellationToken ct)
        => _editorSupport.WebView.PreparePageAsync(html, ct);

    /// <summary>復帰要求が出ていれば本文差し替えを使わせない（必ず html が組み上がる＝1回で収束する）。</summary>
    string? IEditorSupportRenderHost.ReadyPageKey
        => _editorSupportForceFullPage ? null : _editorSupport.WebView.ReadyPageKey;

    void IEditorSupportRenderHost.ClearFullPageRequest() => _editorSupportForceFullPage = false;

    LspNoticeModel.Notice? IEditorSupportRenderHost.DiagnoseLsp(string filePath) {
        var prompt = EvaluateLspPrompt(filePath);
        var failure = prompt is null ? EvaluateLspFailure(filePath) : null;
        return prompt is null && failure is null ? null : LspNoticeModel.Build(prompt, failure);
    }

    void IEditorSupportRenderHost.ScheduleLspReadyRetry() => ScheduleCodeReadyRetry();
    void IEditorSupportRenderHost.StopLspReadyRetry() => StopCodeReadyRetry();

    private void InstallLspForEditorSupportSource()
    {
        var filePath = _editorSupport.Source?.Control.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return;
        var info = EvaluateLspPrompt(filePath);
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
    /// 描画の入れ子（tick が await 中に次の tick が走って解析要求が積み上がる）が起きない。</summary>
    private void CodeReadyRetry_Tick(object? sender, EventArgs e) {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null || !_codeSupport.CanHandle(filePath) || _editorSupport.OutlineRoots is not null) {
            StopCodeReadyRetry();
            return;
        }
        if (_editorSupport.AdvanceReadyAttempt() > CodeReadyMaxRetries) {
            StopCodeReadyRetry(); // サーバーが来ない：案内のまま諦める（ペイン再オープンで再試行される）
            return;
        }
        var lsp = GetLspDocument(source);
        var ready = lsp is not null && lsp.IsReady
                    && CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath);
        // 準備できた瞬間と、猶予を過ぎて案内を出す一度だけ描き直す（毎 tick 投げると無駄に組み直す）。
        if (ready || _editorSupport.ReadyAttempts == EditorSupportRenderFlow.ConnectingNoticeGraceTicks)
            InvalidateEditorSupport();
    }
    private void ScheduleCodeCallPanelsRefresh()
        => _editorSupport.ScheduleCaretRefresh(() => InvalidateEditorSupport(EditorSupportUpdateReason.Caret));
    private void EditorSupportSource_CaretMoved(object? sender, CaretInfo e)
    {
        // current 表示はアウトラインの純ロジックだけで決まる。150ms のデバウンスや
        // references/callHierarchy の応答を待たず、キャレット移動と同時に付け替える。
        if (_editorSupport.OutlineRoots is { } roots
            && ReferenceEquals(_editorSupport.OutlineSource, _editorSupport.Source))
        {
            var member = CodeOutline.FindEnclosing(roots, e.Line, e.Column);
            _editorSupport.OutlineView?.SetCurrent(member is null ? 0 : member.Line0 + 1);
        }
        ScheduleCodeCallPanelsRefresh();
    }
    private void ToggleMarkdownTaskCheckbox(int lineIndex) {
        var source = _editorSupport.Source;
        if (source is null)
            return;
        var text = source.Control.Text;
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length)
            return;
        var toggled = MarkdownRenderer.ToggleTaskListLine(lines[lineIndex]);
        if (toggled is null)
            return;
        lines[lineIndex] = toggled;
        _syncingEditorFromSupport = true;
        try { source.Control.SetText(string.Join(eol, lines)); }
        finally { _syncingEditorFromSupport = false; }
        ScheduleEditorSupportUpdate();
    }
    private bool IsEditorSupportInThumbnail() {
        if (!IsSessionEnabled(PaneKind.EditorSupport))
            return false;
        if (_stageActive)
            return _overviewActive || _stagePane != PaneKind.EditorSupport;
        return !IsShownInMain(PaneKind.EditorSupport);
    }
}
