
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: EditorSupport ペイン（Markdown プレビュー等の表示・スクロール同期）。
/// 自動表示はしない（明示操作で開いたときだけアクティブエディタに追従して描く）。</summary>
public partial class ShellWindow {
    private async Task OpenEditorSupportAsync(EditorTab sourceTab) {
        await SwitchEditorSupportSourceAsync(sourceTab, force: true);
        if (_stageActive)
            SetStagePane(PaneKind.EditorSupport);   // ソロは舞台へ立てる
        else
            ShowEditorSupportPane();                 // タイルは Editor の右隣へ開く
        await UpdateEditorSupportAsync();
        RecordTrailPreview(sourceTab);
    }

    private async void OnEditorSupportBack(object sender, RoutedEventArgs e) => await EditorSupportGoBackAsync();

    private void OnShellPreviewMouseNavigate(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.XButton1) {
            e.Handled = true;
            _ = EditorSupportGoBackAsync();
        } else if (e.ChangedButton == MouseButton.XButton2) {
            e.Handled = true;
            _ = EditorSupportGoForwardAsync();
        }
    }

    private Task EditorSupportGoBackAsync() => EditorSupportNavigateHistoryAsync(back: true);
    private Task EditorSupportGoForwardAsync() => EditorSupportNavigateHistoryAsync(back: false);

    private async Task EditorSupportNavigateHistoryAsync(bool back) {
        await _editorSupport.NavigateHistoryAsync(back, _editorTabs, tab => ActivateEditorTab(tab.Id), path => OpenFileInNewEditorTabAsync(path));
        UpdateEditorSupportNavAffordances();
    }

    private void UpdateEditorSupportNavAffordances() {
        if (EditorSupportBackButton is not null)
            EditorSupportBackButton.IsEnabled = _editorSupport.History.CanGoBack;
    }

    private async Task SwitchEditorSupportSourceAsync(EditorTab sourceTab, bool force = false) {
        if (!_editorSupport.TryChangeSource(sourceTab, force, out var previous))
            return;

        if (previous is not null) {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        StopCodeReadyRetry();

        UpdateEditorSupportNavAffordances();
        sourceTab.Control.ViewportScrolled += EditorSupportSource_ViewportScrolled;
        sourceTab.Control.CaretMoved += EditorSupportSource_CaretMoved;
        UpdateEditorSupportPinToggle();

        await UpdateEditorSupportAsync();
    }

    private async void OnToggleEditorSupportPin(object sender, RoutedEventArgs e) {
        _editorSupport.IsPinned = EditorSupportPinToggle.IsChecked == true;
        UpdateEditorSupportPinToggle();

        if (_editorSupport.IsPinned) {
            if (_editorSupport.Source is null && _activeEditorTab is not null)
                await SwitchEditorSupportSourceAsync(_activeEditorTab, force: true);
            return;
        }

        if (_activeEditorTab is not null)
            await SwitchEditorSupportSourceAsync(_activeEditorTab, force: true);
    }

    private async void OnToggleEditorSupportSlideMode(object sender, RoutedEventArgs e) {
        _settings.Appearance.MarkdownSlideMode = EditorSupportSlideToggle.IsChecked == true;
        await UpdateEditorSupportAsync();
    }

    private async void OnOpenEditorSupportInBrowser(object sender, RoutedEventArgs e) {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;

        var provider = _editorSupports.Resolve(filePath);
        if (provider is IEditorSupportUriProvider uriProvider)
        {
            await OpenUrlInBrowserAsync(uriProvider.ResolveNavigationUri(filePath), uriProvider.DescribeTitle(filePath));
            return;
        }

        if (provider is not IEditorSupportHtmlProvider htmlProvider)
            return; // ビジュアル提供者（CSV/TSV グリッド等）や対応の無いファイルは開ける HTML が無い。

        var title = htmlProvider.DescribeTitle(filePath);
        var mapFolder = MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder;
        var htmlText = htmlProvider.UsesEditorText ? source.Control.Text : string.Empty;
        var html = await Task.Run(() => htmlProvider.RenderHtml(filePath, htmlText));

        await OpenEditorSupportSnapshotInBrowserAsync(html, mapFolder, title);
    }

    private void UpdateEditorSupportPinToggle() {
        EditorSupportPinToggle.IsChecked = _editorSupport.IsPinned;
        EditorSupportPinToggle.ToolTip = _editorSupport.IsPinned
            ? "ピン留めを解除してアクティブなエディタに追従"
            : "現在のサポート対象にピン留め";
    }

    private void UpdateEditorSupportHeaderButtons(bool showSlide, bool showOpenInBrowser, bool showExport) {
        EditorSupportSlideToggle.Visibility = showSlide ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportOpenInBrowserButton.Visibility = showOpenInBrowser ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportExportButton.Visibility = showExport ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleEditorSupportUpdate() {
        if (_editorSupport.Source is null)
            return;

        if (_editorSupportDebounceTimer is null) {
            _editorSupportDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _editorSupportDebounceTimer.Tick += async (s, _) => {
                ((DispatcherTimer)s!).Stop();
                await UpdateEditorSupportAsync();
            };
        }

        _editorSupportDebounceTimer.Stop();
        _editorSupportDebounceTimer.Start();
    }

    private async Task UpdateEditorSupportAsync() {
        var source = _editorSupport.Source;
        if (source is null)
            return;

        var filePath = source.Control.FilePath;
        var provider = _editorSupports.Resolve(filePath);

        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender( onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        if (provider is null && filePath is not null && _codeSupport.CanHandle(filePath)) {
            UpdateEditorSupportHeaderButtons(showSlide: false, showOpenInBrowser: false, showExport: false);
            await UpdateCodeEditorSupportAsync(source, filePath);
            return;
        }

        var visual = provider as IEditorSupportVisualProvider;
        if (visual is null && provider is null && filePath is not null && BinaryFileDetector.IsBinary(filePath))
            visual = _hexSupport;

        if (visual is not null && filePath is not null) {
            UpdateEditorSupportHeaderButtons(showSlide: false, showOpenInBrowser: false, showExport: false);
            EditorSupportTitle.Text = visual.DescribeTitle(filePath);
            await _editorSupport.ShowVisualAsync( EditorSupportContentHost, visual, filePath, source.Control.Text, EditorSupportVisual_ContentEdited);
            return;
        }

        HideEditorSupportVisual();

        var text = (provider?.UsesEditorText ?? true) ? source.Control.Text : string.Empty;

        var seq = _editorSupport.BeginRender();

        var content = await _editorSupport.PrepareWebContentAsync( provider, filePath, text, _workspace.RootPath ?? string.Empty, _editorSupport.WebView.ReadyPageKey, _settings.Appearance.MarkdownPreviewTheme);
        if (!_editorSupport.IsLatestRender(seq))
            return;
        UpdateEditorSupportHeaderButtons(content.ShowSlide, content.ShowOpenInBrowser, content.ShowExport);
        _editorSupport.WebView.SetPending( content.Html, content.Body, content.Uri, content.MapFolder, content.PageKey);
        EditorSupportTitle.Text = content.Title;

        var view = await EnsureEditorSupportViewAsync();
        if (view?.CoreWebView2 is null)
            return;

        if (!_editorSupport.IsLatestRender(seq))
            return;

        RenderPendingEditorSupportContent(view.CoreWebView2);
    }

    private async Task UpdateCodeEditorSupportAsync(EditorTab source, string filePath, bool fromReadyRetry = false) {
        var seq = _editorSupport.BeginRender();
        var lsp = GetLspManager(source);

        var ready = lsp is not null && lsp.IsConnected && lsp.IsDocumentReady && LspMatchesFile(lsp, filePath);

        if (CodeSupportDiag.IsEnabled) {
            if (!fromReadyRetry)
                _editorSupport.DiagnosticStopwatch = System.Diagnostics.Stopwatch.StartNew();
            CodeSupportDiag.Log( $"enter file={Path.GetFileName(filePath)} ready={ready} " +
                $"lsp={(lsp is null ? "null" : "ok")} connected={lsp?.IsConnected} docReady={lsp?.IsDocumentReady} " +
                $"match={(lsp is not null && LspMatchesFile(lsp, filePath))} " +
                $"elapsed={_editorSupport.DiagnosticStopwatch?.ElapsedMilliseconds ?? 0}ms retryTick={_editorSupport.ReadyAttempts}");
        }

        var view = EnsureCodeOutlineView();
        void ShowCodeView() {
            ShowEditorSupportVisual(view);
            EditorSupportTitle.Text = _codeSupport.DescribeTitle(filePath);
        }

        if (!ready) {
            _editorSupport.ClearOutline();

            var prompt = _lspManagement.EvaluateForFile(filePath);
            if (prompt is not null || _editorSupport.ReadyAttempts >= CodeConnectingNoticeGraceTicks) {
                ShowCodeView();
                view.ShowNotice(LspNoticeModel.Build(prompt));
            }
            ScheduleCodeReadyRetry();
            return;
        }

        ShowCodeView();

        StopCodeReadyRetry();
        CodeSupportDiag.Log($"ready reached after {_editorSupport.DiagnosticStopwatch?.ElapsedMilliseconds ?? 0}ms");

        var symbolsSw = CodeSupportDiag.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var symbols = await RequestDocumentSymbolsSafeAsync(lsp!);
        CodeSupportDiag.Log($"documentSymbols {symbolsSw?.ElapsedMilliseconds ?? 0}ms count={symbols.Count}");

        if (!_editorSupport.IsLatestRender(seq))
            return;

        var caret = source.Control.Caret;
        var roots = CodeEditorSupport.ToOutline(symbols, SplitLines(source.Control.Text));

        if (roots.Count > 0) {
            var currentLine1 = CurrentMemberLine1(roots, caret);
            _editorSupport.SetOutline(source, roots);
            _editorSupport.CurrentSymbolRange = null;                       // ②は未取得（この後 SetCurrentAndPanels で埋める）
            _editorSupport.CurrentCaret = (caret.Line, caret.Column);
            view.ShowOutline(roots, currentLine1, CallPanels.Empty);   // 構造だけ先に（②は待たない）
            LogOutlineShown("structure");
        } else {
            view.ShowNotice(LspNoticeModel.Build(null));
        }

        var panelsSw = CodeSupportDiag.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var (panels, symbolRange) = await FetchCallPanelsAsync(lsp!, caret.Line, caret.Column);
        CodeSupportDiag.Log( $"callPanels {panelsSw?.ElapsedMilliseconds ?? 0}ms " +
            $"in={panels.Incoming.Count} out={panels.Outgoing.Count} refs={panels.References.Count}");
        if (!_editorSupport.IsLatestRender(seq))
            return;

        if (roots.Count > 0) {
            var currentLine1 = CurrentMemberLine1(roots, caret);
            _editorSupport.CurrentSymbolRange = symbolRange;
            _editorSupport.CurrentCaret = (caret.Line, caret.Column);
            view.SetCurrentAndPanels(currentLine1, panels);
            LogOutlineShown("panels");
            return;
        }

        for (var attempt = 0; attempt < CodeColdStructureRetries; attempt++)
        {
            symbols = await RequestDocumentSymbolsSafeAsync(lsp!);
            if (!_editorSupport.IsLatestRender(seq))
                return;
            roots = CodeEditorSupport.ToOutline(symbols, SplitLines(source.Control.Text));
            if (roots.Count > 0)
                break;
            await Task.Delay(CodeColdStructureRetryDelay);
            if (!_editorSupport.IsLatestRender(seq))
                return;
        }
        CodeSupportDiag.Log($"cold structure refetch count={roots.Count}");

        var current = CurrentMemberLine1(roots, caret);
        _editorSupport.SetOutline(source, roots);
        _editorSupport.CurrentSymbolRange = symbolRange;
        _editorSupport.CurrentCaret = (caret.Line, caret.Column);
        view.ShowOutline(roots, current, panels);
        LogOutlineShown(roots.Count > 0 ? "cold-structure+panels" : "empty");
    }

    private const int CodeColdStructureRetries = 6;

    private static readonly TimeSpan CodeColdStructureRetryDelay = TimeSpan.FromMilliseconds(300);

    private static async Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsSafeAsync(IEditorLspManager lsp)
        => await CodeEditorSupportAnalysis.RequestDocumentSymbolsSafeAsync(lsp);

    private static int CurrentMemberLine1(IReadOnlyList<OutlineNode> roots, CaretInfo caret)
        => CodeEditorSupportAnalysis.CurrentMemberLine1(roots, caret);

    private void LogOutlineShown(string phase) {
        if (_editorSupport.DiagnosticStopwatch is not null)
            CodeSupportDiag.Log($"shown[{phase}], TOTAL {_editorSupport.DiagnosticStopwatch.ElapsedMilliseconds}ms");
    }

    private CodeOutlineView EnsureCodeOutlineView() {
        if (_editorSupport.OutlineView is not null)
            return _editorSupport.OutlineView;

        var view = new CodeOutlineView();
        view.SourceLineActivated += (_, line1) => FocusEditorSupportSource(line1 > 0 ? line1 : null, alignTop: true);
        view.FileLocationActivated += (_, e) => _ = OpenPathInEditorAsync(e.Path, e.Line1, column: 0, alignTop: true);
        view.InstallRequested += (_, _) => InstallLspForEditorSupportSource();
        view.OpenLspSettingsRequested += (_, _) => _vm.LspPrompt.OpenSettingsCommand.Execute(null);
        view.OpenDocsRequested += (_, url) => _ = OpenUrlInBrowserAsync(url, null);

        _editorSupport.OutlineView = view;
        return view;
    }

    private static bool LspMatchesFile(IEditorLspManager lsp, string filePath)
        => CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath);

    private static Task<(CallPanels Panels, LspRange? SymbolRange)> FetchCallPanelsAsync( IEditorLspManager lsp, int line0, int col0)
        => CodeEditorSupportAnalysis.FetchCallPanelsAsync(lsp, line0, col0);

    private static IReadOnlyList<string> SplitLines(string? text)
        => CodeEditorSupportAnalysis.SplitLines(text);

    private async Task RefreshCodeCallPanelsAsync() {
        var source = _editorSupport.Source;
        if (source is null)
            return;

        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender( onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        var caret = source.Control.Caret;
        if (!_editorSupport.ShouldRefreshCallPanels(source, caret))
            return;
        var roots = _editorSupport.OutlineRoots!;

        var filePath = source.Control.FilePath;
        if (filePath is null)
            return;
        var lsp = GetLspManager(source);
        if (lsp is null || !lsp.IsConnected || !lsp.IsDocumentReady || !LspMatchesFile(lsp, filePath))
            return;

        var seq = _editorSupport.BeginRender();
        var (panels, symbolRange) = await FetchCallPanelsAsync(lsp, caret.Line, caret.Column);
        if (!_editorSupport.IsLatestRender(seq))
            return;

        var member = CodeOutline.FindEnclosing(roots, caret.Line, caret.Column);
        var currentLine1 = member is null ? 0 : member.Line0 + 1; // current を付け替える行（1 始まり）

        _editorSupport.CurrentSymbolRange = symbolRange;
        _editorSupport.CurrentCaret = (caret.Line, caret.Column);
        _editorSupport.OutlineView?.SetCurrentAndPanels(currentLine1, panels);
    }

    private void InstallLspForEditorSupportSource()
    {
        var filePath = _editorSupport.Source?.Control.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return;

        var info = _lspManagement.EvaluateForFile(filePath);
        if (info is null)
            return; // 既に導入済み等（案内ボタンが古い）：何もしない

        _lspManagement.InstallForPrompt(info);
    }

    private static readonly TimeSpan CodeReadyRetryInterval = TimeSpan.FromMilliseconds(200);

    private const int CodeReadyMaxRetries = 125;

    private const int CodeConnectingNoticeGraceTicks = 8;

    private void ScheduleCodeReadyRetry()
        => _editorSupport.ScheduleReadyRetry(CodeReadyRetryInterval, CodeReadyRetry_Tick);

    private void StopCodeReadyRetry()
        => _editorSupport.StopReadyRetry();

    private async void CodeReadyRetry_Tick(object? sender, EventArgs e) {
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

        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender( onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        var lsp = GetLspManager(source);
        var ready = lsp is not null && lsp.IsConnected && lsp.IsDocumentReady && LspMatchesFile(lsp, filePath);
        if (!ready) {
            if (_editorSupport.ReadyAttempts == CodeConnectingNoticeGraceTicks)
                await UpdateCodeEditorSupportAsync(source, filePath, fromReadyRetry: true);
            return;
        }

        await UpdateCodeEditorSupportAsync(source, filePath, fromReadyRetry: true);
    }

    private void ScheduleCodeCallPanelsRefresh()
        => _editorSupport.ScheduleCaretRefresh(RefreshCodeCallPanelsAsync);

    private void EditorSupportSource_CaretMoved(object? sender, CaretInfo e)
        => ScheduleCodeCallPanelsRefresh();

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
