namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン内分割（vim 風 Ctrl+W v/s/q）と外観適用・PaneSplitView 実装</summary>
public partial class ShellWindow {
    private readonly WorkspaceEditTransactionCoordinator _workspaceEditTransactions = new();

    private bool CloseFocusedViewport() {
        var pane = _focusedRegion?.Pane;
        if (!ViewportSplitPolicy.CanCloseFocused(
                pane, _editorViews?.LeafCount ?? 0, _terminalViews?.LeafCount ?? 0))
            return false;
        if (pane == PaneKind.Editor) CloseEditorView();
        else CloseTerminalView();
        return true;
    }
    private void HandleViewportSplitKey(Key key) {
        var input = key switch {
            Key.V => ViewportSplitKey.Vertical,
            Key.S => ViewportSplitKey.Horizontal,
            _ => ViewportSplitKey.Close,
        };
        if (ViewportSplitPolicy.ResolveKey(_focusedRegion?.Pane, input) is not { } action)
            return;
        if (action.Pane == PaneKind.Editor) {
            if (action.Orientation is { } orientation) SplitEditorView(orientation);
            else CloseEditorView();
        } else if (action.Orientation is { } terminalOrientation) SplitTerminalView(terminalOrientation);
        else CloseTerminalView();
    }
    private void SplitEditorView(SplitKind orientation, string? filePath = null) {
        if (_editorViews is null)
            return;
        var src = _editorViews.FocusedTabId is { } sid
            ? _editorTabs.FirstOrDefault(t => t.Id == sid)
            : _activeEditorTab;
        var openPath = ViewportSplitPolicy.ResolveEditorPath(
            filePath, src?.Control.FilePath, _activeWorkspace?.RootPath, _terminal.CurrentDirectory);
        var newTab = CreateEditorTab();
        _editorTabs.Add(newTab);
        _vm.Tabs.AddEditorTab(newTab.Id, openPath ?? src?.Control.FilePath, src?.Control.IsModified ?? false, false);
        var splitContent = ViewportSplitPolicy.ResolveEditorSplitContent(
            openPath, src?.Control.FilePath, src?.Control.IsModified ?? false, src?.Control.Text);
        if (splitContent.FilePath is { } splitPath) LoadEditorFile(newTab.Control, splitPath);
        else if (splitContent.Text is { } text) newTab.Control.SetText(text);
        _editorViews.SplitFocused(orientation, newTab.Id);
        SetActiveEditorTab(newTab);
        UpdateEditorTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }
    private async Task OpenEditorTabFromEditorAsync(string? filePath) {
        var openPath = ViewportSplitPolicy.ResolveEditorPath(
            filePath, _activeEditorTab?.Control.FilePath, _activeWorkspace?.RootPath, _terminal.CurrentDirectory);
        if (openPath is not null)
        {
            await OpenFileInNewEditorTabAsync(openPath);
            return;
        }
        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        UpdateEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }
    private void CycleEditorTab(int step) {
        var index = _activeEditorTab is { } active ? _editorTabs.FindIndex(t => t.Id == active.Id) : 0;
        if (ViewportSplitPolicy.NextTabIndex(index, _editorTabs.Count, step) is not { } next)
            return;
        ActivateEditorTab(_editorTabs[next].Id);
    }
    private void CloseActiveEditorTab() {
        if (_activeEditorTab is not { } active)
            return;
        CloseEditorTab(active.Id);
        SaveActiveWorkspaceSnapshot();
    }
    private void CloseEditorView() {
        if (_editorViews?.CloseFocused() != true)
            return;
        if (_editorViews.FocusedTabId is { } id && _editorTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SetActiveEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }
    private void SplitTerminalView(SplitKind orientation) {
        if (_terminalViews is null)
            return;
        var src = _terminalViews.FocusedTabId is { } sid
            ? _terminalTabs.FirstOrDefault(t => t.Id == sid)
            : _activeTerminalTab;
        var cwd = ViewportSplitPolicy.ResolveTerminalDirectory(
            src?.View.WorkingDirectory, _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory);
        var newTab = CreateTerminalTab(cwd);
        _terminalTabs.Add(newTab);
        _vm.Tabs.AddTerminalTab(newTab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);
        _terminalViews.SplitFocused(orientation, newTab.Id);
        SetActiveTerminalTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }
    private void CloseTerminalView() {
        if (_terminalViews?.CloseFocused() != true)
            return;
        if (_terminalViews.FocusedTabId is { } id && _terminalTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SetActiveTerminalTab(tab);
        SaveActiveWorkspaceSnapshot();
    }
    private TerminalTab CreateTerminalTab(string startDirectory, Guid? requestedId = null) {
        var view = new TerminalTabView("pwsh.exe", startDirectory) {
            AutoFocusOnStart = false, };
        // 端末のシェルは Loaded を合図に ConPTY を Task.Run で起こす。そこがプールの行列に嵌まると
        // 窓は出ているのにプロンプトだけ数秒遅れるので、待ち行列の深さごと起動プロファイルに残す
        // （§31.16／ChildProcessIo）。Loaded はペインの再ペアレントのたびに飛ぶので、記録するのは
        // シェルが実際に起こされる最初の一度だけ——そうしないとペイン切替のたびに起動のログが汚れる。
        void MarkFirstLoad(object? _, RoutedEventArgs __) {
            view.Loaded -= MarkFirstLoad;
            StartupProfiler.Mark(
                $"  端末:Loaded（シェル起動の合図）待ち={System.Threading.ThreadPool.PendingWorkItemCount}");
        }
        view.Loaded += MarkFirstLoad;
        _appearance.ApplyTerminalAppearance(view);
        return HookTerminalTab(new TerminalTab(requestedId ?? Guid.NewGuid(), view));
    }
    /// <summary>メインのタブとしての配線（見出し追従・リンク・右クリック・活動バッジ）を張る。
    /// 新しいタブと、切り離しウィンドウから<b>戻ってきた</b>セッションの受け入れで共通。</summary>
    private TerminalTab HookTerminalTab(TerminalTab tab) {
        tab.View.HeaderTitleChanged += (_, title) => UpdateTerminalTab(tab, title);
        tab.View.HyperlinkActivated += OnTerminalLinkActivated;
        tab.View.ContextMenuBuilding += OnTerminalContextMenuBuilding;
        HookTerminalActivity(tab);
        return tab;
    }
    private EditorTab CreateEditorTab(Guid? requestedId = null) =>
        new(requestedId ?? Guid.NewGuid()) { Realizer = RealizeEditorControl };
    private EditorTab CreatePendingEditorTab(EditorTabSnapshot snapshot) =>
        new(snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id) {
            Realizer = RealizeEditorControl, Pending = snapshot
        };
    private void RealizeEditorControl(EditorTab tab) {
        var control = BuildEditorControl(tab);
        tab.SetControl(control);
        if (tab.Pending is { } snapshot) {
            // セッション復元だけは LoadEditorFile を通らず editor.LoadFile を直に呼ぶ（未保存本文の復元があるため）。
            // そちらもグリフを捨てるので、ここで送り直す。
            WorkspaceSessionCoordinator.RestoreEditor(control, snapshot);
            _appearance.ApplyUsingFoldingOnOpen(control);
            SyncEditorTestGlyphs(control);
            ScheduleStyleCopAnalysis(control);
            tab.Pending = null;
        }
    }
    /// <summary>このタブが今持っている LSP 文書ハンドル。未実体化・対応サーバー無しなら null。
    /// 文書スコープの問い合わせ（アウトライン・参照）はこれを使い、ワークスペーススコープは
    /// <c>_lspWorkspace</c> へ直接投げる。</summary>
    private ILspDocument? GetLspDocument(EditorTab tab) =>
        tab.IsRealized ? tab.Control.LspDocument : null;

    /// <summary>未実体化／Untitledタブの空パスを通常ファイルとして正規化しない。</summary>
    private static bool EditorPathMatches(VimEditorControl editor, string? path)
        => ViewportSplitPolicy.EditorPathMatches(editor.FilePath, path);

    /// <summary>
    /// エディタからの先読み要求を FIM エンジンへ渡す。打鍵のたびに呼ばれ、古い要求は
    /// エディタ側がキャンセルする。出せないときは null を返すだけ——先読みの失敗で
    /// 入力が止まることがあってはならない。
    /// </summary>
    private async Task<Editor.Core.Completion.InlineSuggestion?> RequestInlineSuggestionAsync(
        Editor.Core.Completion.InlineSuggestionContext context, CancellationToken ct)
    {
        var text = await _fimCompletion.CompleteAsync(
            _settings.InlineCompletion, context.Lines, context.Line, context.Column, ct);
        return text is null
            ? null
            : new Editor.Core.Completion.InlineSuggestion(text, Editor.Core.Completion.InlineSuggestionSource.External);
    }

    private VimEditorControl BuildEditorControl(EditorTab tab) {
        var control = new VimEditorControl(new VimEditorControlOptions {
            GitServiceFactory = () => new GitDiffProvider(),
            // キャレットの先に薄く出す提案のうち、ローカル LLM（FIM）が作る方。
            // エディタ内蔵の予測（既出行からの補完）はこれが無くても動き、先に出る。
            InlineSuggestionProvider = RequestInlineSuggestionAsync,
            // ワークスペースフォルダーも文書の参照カウントもサーバーのプールもワークスペース側が知っている。
            LspWorkspace = _lspWorkspace, LspServerAdmin = _lspServerAdmin,
            EngineServices = _editorEngineServices,
            // 「名前の変更」は Loomo の「リファクタリング」サブメニューに入れる（§32）。
            // これを渡さないとコントロール側の "Rename Symbol" と2つ並ぶ。
            HostProvidesRenameMenuItem = true,
            // ネイティブ項目の見出しを日本語にする（Loomo 側の追加項目と同じ言語で並べる）。
            ContextMenuLabels = Services.EditorMenuLabels.Japanese,
            // LSPがrenameを返さない／接続できない場合も、C#専用DLLのRoslyn意味モデルへ戻す。
            HostRenameProvider = (path, source, line, character, newName, ct) =>
                RequestCSharpRenameFallbackAsync(path, source, line, character, newName, ct),
            HostPrepareRenameProvider = (path, source, line, character, ct) =>
                RequestCSharpPrepareRenameFallbackAsync(path, source, line, character, ct),
            HostDefinitionProvider = (path, source, line, character, ct) =>
                RequestCSharpDefinitionFallbackAsync(path, source, line, character, ct),
            HostReferencesProvider = (path, source, line, character, ct) =>
                RequestCSharpReferencesFallbackAsync(path, source, line, character, ct),
            HostImplementationProvider = (path, source, line, character, ct) =>
                RequestCSharpImplementationsFallbackAsync(path, source, line, character, ct),
            HostTypeDefinitionProvider = (path, source, line, character, ct) =>
                RequestCSharpTypeDefinitionFallbackAsync(path, source, line, character, ct),
            HostDeclarationProvider = (path, source, line, character, ct) =>
                RequestCSharpDeclarationFallbackAsync(path, source, line, character, ct),
            HostCompletionProvider = (path, source, line, character, ct) =>
            {
                var openTexts = FindOpenCSharpEditorTexts();
                return sk0ya.Loomo.CSharp.Editor.CSharpCompletionService.GetAsync(
                    _solutionModel?.Current, path, source, line, character, ct, openTexts);
            },
            HostSemanticTokensProvider = (path, source, ct) =>
                sk0ya.Loomo.CSharp.Editor.CSharpSemanticTokenService.GetAsync(
                    _solutionModel?.Current, path, source, ct, FindOpenCSharpEditorTexts()),
            HostHoverProvider = (path, source, line, character, ct) =>
                RequestCSharpHoverFallbackAsync(path, source, line, character, ct),
            HostDocumentHighlightProvider = (path, source, line, character, ct) =>
            {
                if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<IReadOnlyList<Editor.Core.Lsp.DocumentHighlight>>([]);
                return sk0ya.Loomo.CSharp.Editor.CSharpDocumentHighlightService.FindAsync(
                    _solutionModel?.Current, path, source,
                    new Editor.Core.Lsp.LspPosition(line, character),
                    FindOpenCSharpEditorTexts(), ct);
            },
            // LSPが署名情報を返さない場合も、Roslynの意味モデルでC#の
            // overload／active parameter／XML documentationを表示する（§33.11）。
            HostSignatureHelpProvider = (path, source, line, character, ct) =>
            {
                var openTexts = FindOpenCSharpEditorTexts();
                return Task.Run(() => sk0ya.Loomo.CSharp.Editor.CSharpSignatureHelpService.Get(
                    _solutionModel?.Current, path, source, line, character, openTexts), ct);
            },
            HostInlayHintProvider = (path, source, startLine, endLine, ct) =>
            {
                var openTexts = FindOpenCSharpEditorTexts();
                return Task.Run(() => sk0ya.Loomo.CSharp.Editor.CSharpParameterNameHintService.Get(
                    _solutionModel?.Current, path, source, startLine, endLine, openTexts), ct);
            },
            // 診断の文面は結論だけのことがある。「なぜ」を知っているのはプロジェクトを
            // 抱えている側なので、部屋が答える（§30.19）。
            HostDiagnosticExplanationProvider = (path, source, diagnostic, ct) =>
            {
                if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<string?>(null);
                var solution = _solutionModel?.Current;
                return Task.Run(() => _diagnosticExplanations.Explain(solution, path, source, diagnostic), ct);
            }
        }) {
            VimEnabled = _settings.Vim.Enabled, Visibility = Visibility.Collapsed
        };
        control.HostCodeActionProvider = (range, only) =>
            QuickFixCoordinator.RequestAsync(control, range, only);
        _appearance.ApplyEditorOptions(control);
        _appearance.ApplyEditorAppearance(control);
        control.SetSharedStatusBar(EditorSharedStatusBar);
        control.BufferChanged += (_, _) => {
            _workspaceEditTransactions.OnEditorBufferChanged();
            UpdateEditorTab(tab);
            _trailEditCommit.Request(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                ScheduleEditorSupportUpdate();
            ScheduleStyleCopAnalysis(control);
        };
        control.LspDiagnosticsChanged += OnStyleCopLspDiagnosticsChanged;
        control.SaveRequested += (_, _) => {
            QueueEditorTabUpdate(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                ScheduleEditorSupportUpdate();
        };
        control.MarkdownPreviewRequested += (_, _) => OpenEditorSupport(tab);
        control.LinkClicked += OnEditorLinkClicked;
        control.FileLinkClicked += OnEditorFileLinkClicked;
        // LSP の定義ジャンプが別ファイルを返した場合、Editor.Controls はこのイベントを発火する。
        // ここを購読しないと同一ファイル内のジャンプだけ動き、別ファイルの定義へ移動できない。
        // Editor.Controls の位置は 0 始まり、OpenPathInEditorAsync は 1 始まりなので変換する。
        // 「位置なし」は負値だけ——0 は 1 行目・1 桁目という正当な位置で、ここを > 0 で弾くと
        // ファイル先頭に宣言されたシンボルへのジャンプだけキャレットが動かない。
        control.OpenFileRequested += (_, e) => _ = OpenPathInEditorAsync(
            e.FilePath,
            e.Line >= 0 ? e.Line + 1 : 0,
            e.Column >= 0 ? e.Column + 1 : 0);
        control.FindReferencesResult += OnEditorFindReferencesResult;
        control.WorkspaceEditRequested += OnEditorWorkspaceEditRequested;
        control.ContextMenuBuilding += OnEditorContextMenuBuilding;
        control.BlameCommitClicked += (_, e) => ShowBlameCommitDiff(control, e.Blame);
        control.SplitRequested += (_, e) => SplitEditorView(e.Vertical ? SplitKind.Columns : SplitKind.Rows, e.FilePath);
        control.NewTabRequested += async (_, e) => await OpenEditorTabFromEditorAsync(e.FilePath);
        control.NextTabRequested += (_, _) => CycleEditorTab(+1);
        control.PrevTabRequested += (_, _) => CycleEditorTab(-1);
        control.CloseTabRequested += (_, _) => CloseActiveEditorTab();
        control.WindowCloseRequested += (_, _) => CloseEditorView();
        WireEditorForDebug(control);
        WireEditorForTestGlyphs(control);
        return control;
    }
    private void OnEditorWorkspaceEditRequested(object? sender, WorkspaceEditRequestedEventArgs e) {
        var currentPreview = e.CurrentFilePath is { Length: > 0 } path &&
            e.CurrentOriginalText is { } original && e.CurrentUpdatedText is { } updated
            ? new WorkspaceEditPreviewFile(path, original, updated)
            : null;
        var outcome = ApplyLspWorkspaceEdit(e.Changes, e.DocumentVersions, e.FileOperations,
            currentPreview, e.ExpectedTexts);
        // 取り消しは失敗ではない。エディタ側もこれを見て「失敗しました」と言わなくなる。
        e.Cancelled = outcome.Cancelled;
        e.Error = outcome.Error;
        e.Handled = !outcome.Cancelled && outcome.Error is null;
    }

    /// <summary>WorkspaceEdit の適用本体はトランザクション coordinator へ委譲する。</summary>
    private sk0ya.Loomo.App.Services.WorkspaceEditOutcome ApplyLspWorkspaceEdit(
        IReadOnlyDictionary<string, IReadOnlyList<Editor.Core.Lsp.LspTextEdit>> changes,
        IReadOnlyDictionary<string, int?>? documentVersions,
        IReadOnlyList<Editor.Core.Lsp.LspFileOperation>? fileOperations,
        WorkspaceEditPreviewFile? currentPreview = null,
        IReadOnlyDictionary<string, string>? expectedTexts = null,
        bool showPreview = true)
        => _workspaceEditTransactions.Apply(changes, documentVersions, fileOperations,
            currentPreview, expectedTexts, _workspace.Folders, _editorTabs,
            EditorPathMatches, ShowWorkspaceEditPreview, showPreview);

    private bool ShowWorkspaceEditPreview(
        IReadOnlyList<WorkspaceEditPreviewFile> files,
        IReadOnlyList<WorkspaceEditPreviewOperation> operations)
    {
        var preview = new WorkspaceEditPreviewDialog("WorkspaceEdit", files, operations) { Owner = this };
        return preview.ShowDialog() == true;
    }

    private bool TryHandleWorkspaceEditUndo(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (_focusedRegion?.Pane != PaneKind.Editor || e.Key != Key.Z ||
            !modifiers.HasFlag(ModifierKeys.Control) ||
            (modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            return false;

        var redo = modifiers.HasFlag(ModifierKeys.Shift);
        var activePath = _activeEditorTab is { IsRealized: true } activeTab
            ? activeTab.Control.FilePath
            : null;
        if (!_workspaceEditTransactions.TryRestoreHistory(redo, activePath, _editorTabs,
            EditorPathMatches, status => EditorSharedStatusBar?.UpdateStatus(status)))
            return false;
        e.Handled = true;
        return true;
    }
    /// <summary>エディタへファイルを読み込ませる。<b>Loomo 側の <c>LoadFile</c> の唯一の漏斗</b>で、
    /// ここを通る経路は現在このとおり:
    /// 新規タブ／プレビュータブで開く（<c>OpenFileInNewEditorTabAsync</c>／<c>OpenFileInPreviewTabAsync</c>）・
    /// 外部変更の読み直し（<c>ReloadExistingTabIfChangedAsync</c> ← 既存タブを開き直したとき／
    /// Git のブランチ切替（<c>RefreshOpenEditorTabsFromDiskAsync</c>）／検索パネルの一括置換）・
    /// 分割で同じファイルを開く（<c>SplitEditorView</c>）・切り離し窓（複製／リンク先を別窓で開く／復元）。
    /// <para>読み込み後の後始末をここへ集める理由は、<c>VimEditorControl.LoadFile</c> が
    /// テストグリフを捨てるのに <c>BufferChanged</c> を<b>発火しない</b>こと。個々の呼び出し側で
    /// 送り直していると、上のどれか（実際にブランチ切替で全タブ）が落ちる。</para></summary>
    private void LoadEditorFile(VimEditorControl control, string path) {
        control.LoadFile(path);
        AfterEditorFileLoaded(control);
    }
    /// <summary>
    /// <see cref="LoadEditorFile"/> の、<b>UI スレッドを空けておく</b>版。ディスクを読むところ——
    /// <c>.editorconfig</c> の探索と解析・バイト列の読み出し・エンコーディング判定・デコード・行分割——は
    /// 背景スレッドで済ませ、UI スレッドが払うのは読み終えたものを載せる部分だけにする（§31.15）。
    /// <para>待っている間にタブが閉じられている／作り直されていることがあるので、載せる前に確かめる。
    /// 閉じたタブのコントロールは <c>Dispose</c> 済みで、そこへ読み込ませると死んだ相手を叩くことになる。</para>
    /// <para>読み込み後の後始末は <see cref="LoadEditorFile"/> と同じ 1 本（<see cref="AfterEditorFileLoaded"/>）
    /// を通る。経路が増えても後始末が分岐しないようにするため。</para>
    /// </summary>
    private async Task LoadEditorFileAsync(EditorTab tab, string path) {
        if (!tab.IsRealized)
            _ = tab.Control;   // 実体化はここで済ませる（await の後に走らせない）
        var control = tab.Control;
        var prepared = await Task.Run(() => PreparedFileLoad.Prepare(path));
        if (!_editorTabs.Contains(tab) || !tab.IsRealized || !ReferenceEquals(tab.Control, control))
            return;
        control.LoadFile(path, prepared);
        AfterEditorFileLoaded(control);
    }
    private void AfterEditorFileLoaded(VimEditorControl control) {
        _appearance.ApplyUsingFoldingOnOpen(control);
        SyncEditorTestGlyphs(control);   // LoadFile はグリフを捨てるが BufferChanged を出さない
        SyncEditorCodeActionBulb(control);
        ScheduleStyleCopAnalysis(control);
    }
    private void ApplyVimEnabledToOpenEditorTabs() {
        foreach (var tab in _editorTabs)
            if (tab.IsRealized)
                tab.Control.VimEnabled = _settings.Vim.Enabled;
    }
    private void ApplyEditorSettingsToOpenEditorTabs() {
        foreach (var tab in _editorTabs) {
            if (!tab.IsRealized) continue;
            _appearance.ApplyEditorOptions(tab.Control);
            _appearance.ApplyUsingFoldingOnOpen(tab.Control);
        }
    }
    private void ApplyAppearanceToOpenTabs() {
        // エディタ以外の面（Diff 本体の構文色）にも同じ配色を配る。エディタだけ塗り替えると色が食い違う。
        EditorSyntaxColors.Apply(_appearance.BuildEditorTheme());
        foreach (var tab in _editorTabs)
            if (tab.IsRealized)
                _appearance.ApplyEditorAppearance(tab.Control);
        foreach (var tab in _terminalTabs)
            _appearance.ApplyTerminalAppearance(tab.View);
        if (_editorSupport.Source is not null)
            ScheduleEditorSupportUpdate();
    }
    private void QueueEditorTabUpdate(EditorTab tab) {
        _ = tab.Control.Dispatcher.BeginInvoke(new Action(() => UpdateEditorTab(tab)));
    }
    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableWidth <= 0)
            return;
        var nextOffset = Math.Clamp( scrollViewer.HorizontalOffset - e.Delta, 0, scrollViewer.ScrollableWidth);
        scrollViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }
}
