namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン内分割（vim 風 Ctrl+W v/s/q）と外観適用・PaneSplitView 実装</summary>
public partial class ShellWindow {
    private bool CloseFocusedViewport() {
        switch (_focusedRegion?.Pane) {
            case PaneKind.Editor when _editorViews is { LeafCount: > 1 }:
                CloseEditorView();
                return true;
            case PaneKind.Terminal when _terminalViews is { LeafCount: > 1 }:
                CloseTerminalView();
                return true;
            default:
                return false;
        }
    }
    private void HandleViewportSplitKey(Key key) {
        switch (_focusedRegion?.Pane) {
            case PaneKind.Editor:
                if (key == Key.V) SplitEditorView(SplitKind.Columns);
                else if (key == Key.S) SplitEditorView(SplitKind.Rows);
                else CloseEditorView();
                break;
            case PaneKind.Terminal:
                if (key == Key.V) SplitTerminalView(SplitKind.Columns);
                else if (key == Key.S) SplitTerminalView(SplitKind.Rows);
                else CloseTerminalView();
                break;
        }
    }
    private void SplitEditorView(SplitKind orientation, string? filePath = null) {
        if (_editorViews is null)
            return;
        var src = _editorViews.FocusedTabId is { } sid
            ? _editorTabs.FirstOrDefault(t => t.Id == sid)
            : _activeEditorTab;
        var openPath = ResolveEditorPath(filePath, src);
        var newTab = CreateEditorTab();
        _editorTabs.Add(newTab);
        _vm.Tabs.AddEditorTab(newTab.Id, openPath ?? src?.Control.FilePath, src?.Control.IsModified ?? false, false);
        if (openPath is not null) {
            LoadEditorFile(newTab.Control, openPath);
        } else if (src is not null) {
            if (!string.IsNullOrWhiteSpace(src.Control.FilePath) && File.Exists(src.Control.FilePath) && !src.Control.IsModified)
                LoadEditorFile(newTab.Control, src.Control.FilePath);
            else
                newTab.Control.SetText(src.Control.Text);
        }
        _editorViews.SplitFocused(orientation, newTab.Id);
        SetActiveEditorTab(newTab);
        UpdateEditorTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }
    private string? ResolveEditorPath(string? filePath, EditorTab? src) {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        if (Path.IsPathRooted(filePath))
            return File.Exists(filePath) ? Path.GetFullPath(filePath) : null;
        var bases = new[] {
            src is { } s && !string.IsNullOrWhiteSpace(s.Control.FilePath)
                ? Path.GetDirectoryName(s.Control.FilePath)
                : null, _activeWorkspace?.RootPath, _terminal.CurrentDirectory, };
        foreach (var dir in bases) {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.GetFullPath(Path.Combine(dir, filePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
    private async Task OpenEditorTabFromEditorAsync(string? filePath) {
        var openPath = ResolveEditorPath(filePath, _activeEditorTab);
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
        if (_editorTabs.Count <= 1)
            return;
        var index = _activeEditorTab is { } active ? _editorTabs.FindIndex(t => t.Id == active.Id) : 0;
        if (index < 0)
            index = 0;
        var count = _editorTabs.Count;
        var next = ((index + step) % count + count) % count;
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
        var cwd = src?.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
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
    {
        if (editor.FilePath is not { Length: > 0 } editorPath || string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private VimEditorControl BuildEditorControl(EditorTab tab) {
        var control = new VimEditorControl(new VimEditorControlOptions {
            GitServiceFactory = () => new GitDiffProvider(),
            // ワークスペースフォルダーも文書の参照カウントもサーバーのプールもワークスペース側が知っている。
            LspWorkspace = _lspWorkspace, LspServerAdmin = _lspServerAdmin,
            EngineServices = _editorEngineServices,
            // 「名前の変更」は Loomo の「リファクタリング」サブメニューに入れる（§32）。
            // これを渡さないとコントロール側の "Rename Symbol" と2つ並ぶ。
            HostProvidesRenameMenuItem = true,
#if LOOMO_EDITOR_HOST_API
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
            {
                var openTexts = FindOpenCSharpEditorTexts();
                return Task.Run(() => sk0ya.Loomo.CSharp.Editor.CSharpHoverService.Get(
                    _solutionModel?.Current, path, source, line, character, openTexts), ct);
            },
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
            }
#endif
        }) {
            VimEnabled = _settings.Vim.Enabled, Visibility = Visibility.Collapsed
        };
#if LOOMO_EDITOR_HOST_API
        control.HostCodeActionProvider = (range, only) =>
            RequestCSharpQuickFixesAsync(control, range, only);
#endif
        _appearance.ApplyEditorOptions(control);
        _appearance.ApplyEditorAppearance(control);
        control.SetSharedStatusBar(EditorSharedStatusBar);
        control.BufferChanged += (_, _) => {
            if (!_restoringWorkspaceEdit)
                _workspaceEditRedo.Clear();
            UpdateEditorTab(tab);
            RecordTrailEdit(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                ScheduleEditorSupportUpdate();
            ScheduleStyleCopAnalysis(control);
        };
#if LOOMO_EDITOR_HOST_API
        control.LspDiagnosticsChanged += OnStyleCopLspDiagnosticsChanged;
#endif
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
#if LOOMO_EDITOR_HOST_API
        control.WorkspaceEditRequested += OnEditorWorkspaceEditRequested;
#endif
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
#if LOOMO_EDITOR_HOST_API
        var currentPreview = e.CurrentFilePath is { Length: > 0 } path &&
            e.CurrentOriginalText is { } original && e.CurrentUpdatedText is { } updated
            ? new WorkspaceEditPreviewFile(path, original, updated)
            : null;
        e.Error = ApplyLspWorkspaceEdit(e.Changes, e.DocumentVersions, e.FileOperations,
            currentPreview, e.ExpectedTexts);
#else
        e.Error = ApplyLspWorkspaceEdit(e.Changes, e.DocumentVersions, e.FileOperations);
#endif
        e.Handled = e.Error is null;
    }
    /// <summary>workspace edit をワークスペースへ適用する。成功なら null、失敗ならユーザーへ出す文言を返す。
    /// 全対象を先に検証し、編集プレビューで確認してからファイル操作と本文変更を行う。
    /// 新規作成／名前変更されたファイルへの本文変更も、仮想的な適用後の内容を先に組み立てる。</summary>
    private string? ApplyLspWorkspaceEdit(
        IReadOnlyDictionary<string, IReadOnlyList<Editor.Core.Lsp.LspTextEdit>> changes,
        IReadOnlyDictionary<string, int?>? documentVersions,
        IReadOnlyList<Editor.Core.Lsp.LspFileOperation>? fileOperations,
        WorkspaceEditPreviewFile? currentPreview = null,
        IReadOnlyDictionary<string, string>? expectedTexts = null) {
        // マルチルート（プライマリ＋追加フォルダー）の全件で判定する。プライマリだけを見ていた頃は、
        // あとから追加したフォルダーのファイルが「ワークスペース外」になり編集ごと失敗していた。
        // 正本は _workspace.Folders——LSP のサーバー自身もこの一覧で initialize されている。
        var folders = _workspace.Folders;
        if (folders.Count == 0)
            return "ワークスペースが開かれていません。";
        Dictionary<string, LspFileSnapshot>? fileSnapshots = null;
        Dictionary<VimEditorControl, string>? editorSnapshots = null;
        var mutationStarted = false;
        try {
            var operations = fileOperations ?? [];
            VerifyExpectedCSharpTexts(expectedTexts, folders);
            ValidateLspFileOperations(operations, folders);
            var plans = new List<(string Path, IReadOnlyList<Editor.Core.Lsp.LspTextEdit> Edits,
                int? Version, List<VimEditorControl> Open, string OriginalText,
                string UpdatedText, string? DiskText, System.Text.Encoding? Encoding)>();
            foreach (var (uri, edits) in changes) {
                var path = LspWorkspaceEditPaths.ResolveInWorkspace(uri, folders);

                int? expectedVersion = null;
                documentVersions?.TryGetValue(uri, out expectedVersion);
                var open = _editorTabs
                    .Where(tab => tab.IsRealized && EditorPathMatches(tab.Control, path))
                    .Select(tab => tab.Control)
                    .ToList();
                if (open.Count > 0) {
                    var openOriginal = open[0].Text;
                    var openUpdated = openOriginal;
                    foreach (var editor in open) {
                        if (expectedVersion is not null && editor.LspDocument?.Version is { } actual && actual != expectedVersion)
                            throw new InvalidOperationException($"{path}: 文書版が一致しません（要求 {expectedVersion} / 現在 {actual}）。");
                        var candidate = VimEditorControl.ApplyTextEdits(editor.Text, edits);
                        if (ReferenceEquals(editor, open[0])) openUpdated = candidate;
                    }
                    plans.Add((path, edits, expectedVersion, open, openOriginal, openUpdated, null, null));
                    continue;
                }
                if (expectedVersion is not null)
                    throw new InvalidOperationException($"{path}: 文書版 {expectedVersion} を検証できません。ファイルを開いて再度実行してください。");
                string original;
                System.Text.Encoding encoding;
                if (File.Exists(path)) {
                    using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                    original = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                } else if (IsCreatedByOperation(path, operations)) {
                    original = "";
                    encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                } else if (FindRenameSource(path, operations) is { } source && File.Exists(source)) {
                    using var reader = new StreamReader(source, detectEncodingFromByteOrderMarks: true);
                    original = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                } else {
                    throw new InvalidOperationException($"{path}: 編集対象のファイルが見つかりません。");
                }
                var updated = VimEditorControl.ApplyTextEdits(original, edits);
                plans.Add((path, edits, null, [], original, updated, updated, encoding));
            }

            var previewFiles = plans
                .Where(plan => !string.Equals(plan.OriginalText, plan.UpdatedText, StringComparison.Ordinal))
                .Select(plan => new WorkspaceEditPreviewFile(plan.Path, plan.OriginalText, plan.UpdatedText))
                .ToList();
            if (currentPreview is not null &&
                !string.Equals(currentPreview.OriginalText, currentPreview.UpdatedText, StringComparison.Ordinal))
                previewFiles.Insert(0, currentPreview);
            var previewOperations = operations.Select(ToPreviewOperation).ToList();
            fileSnapshots = CaptureLspFileSnapshots(operations, plans, currentPreview);
            editorSnapshots = CaptureLspEditorSnapshots(plans, currentPreview);
            if (previewFiles.Count > 0 || previewOperations.Count > 0) {
                var preview = new WorkspaceEditPreviewDialog("WorkspaceEdit", previewFiles, previewOperations)
                { Owner = this };
                if (preview.ShowDialog() != true)
                    return "編集プレビューでキャンセルされました。";
            }

            // Preview中にユーザーや別プロセスが触った場合は、確認済みの差分をそのまま上書きしない。
            VerifyLspTransactionSnapshots(fileSnapshots, editorSnapshots);
            if (operations.Count > 0)
            {
                mutationStarted = true;
                ApplyLspFileOperations(operations, folders);
            }
            foreach (var plan in plans) {
                // 読み取り側を先に同期し、LSPへdidChangeを送るwriterは最後に1回だけ適用する。
                foreach (var editor in plan.Open.OrderBy(editor => editor.LspDocument?.IsWriter == true))
                {
                    mutationStarted = true;
                    if (!editor.TryApplyLspTextEdits(plan.Edits, expectedVersion: null, out var error))
                        throw new InvalidOperationException($"{plan.Path}: {error}");
                }
                if (plan.DiskText is not null)
                {
                    mutationStarted = true;
                    File.WriteAllText(plan.Path, plan.DiskText, plan.Encoding!);
                }
            }
            RecordWorkspaceEditHistory(
                "LSP／Roslyn WorkspaceEdit",
                fileSnapshots,
                CaptureLspFileSnapshots(fileSnapshots.Keys),
                CaptureLspEditorTextSnapshots(editorSnapshots),
                CaptureLspEditorTextSnapshots(editorSnapshots, currentPreview, useCurrentText: true));
            return null;
        }
        catch (Exception ex) {
            // 適用後のI/O失敗でも、既に動かしたEditor／ファイルを確認済みの状態へ戻す。
            // rollback自体の失敗は元の失敗を隠さず、ユーザーに明示する。
            try
            {
                // 検証・キャンセル段階の例外では余計な書き込みをしない。
                if (mutationStarted && fileSnapshots is not null && editorSnapshots is not null)
                    RestoreLspTransactionSnapshots(fileSnapshots, editorSnapshots);
            }
            catch (Exception rollback)
            {
                return $"{ex.Message} 復元にも失敗しました: {rollback.Message}";
            }
            return ex.Message;
        }
    }

    /// <summary>C#専用DLLが編集計画を作った時点の本文を、非同期処理後の適用直前に照合する。
    /// 最新本文へ古い範囲を適用すると、成功して見える破壊的編集になるため、差分表示より前に止める。</summary>
    private void VerifyExpectedCSharpTexts(
        IReadOnlyDictionary<string, string>? expectedTexts,
        IReadOnlyList<string> folders)
    {
        var error = sk0ya.Loomo.CSharp.Refactoring.CSharpEditSnapshotValidator.Validate(
            expectedTexts, folders, path =>
            {
                var editor = _editorTabs
                    .Where(tab => tab.IsRealized && EditorPathMatches(tab.Control, path))
                    .Select(tab => tab.Control)
                    .FirstOrDefault();
                if (editor is not null) return editor.Text;
                return File.Exists(path) ? File.ReadAllText(path) : null;
            });
        if (error is not null) throw new InvalidOperationException(error);
    }

    private sealed record LspFileSnapshot(bool Exists, byte[] Content);

    private static Dictionary<string, LspFileSnapshot> CaptureLspFileSnapshots(
        IReadOnlyList<Editor.Core.Lsp.LspFileOperation> operations,
        IReadOnlyList<(string Path, IReadOnlyList<Editor.Core.Lsp.LspTextEdit> Edits, int? Version,
            List<VimEditorControl> Open, string OriginalText, string UpdatedText, string? DiskText,
            System.Text.Encoding? Encoding)> plans,
        WorkspaceEditPreviewFile? currentPreview = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (LspUri.TryToLocalPath(operation.Uri) is { } path)
                paths.Add(Path.GetFullPath(path));
            if (operation.NewUri is not null && LspUri.TryToLocalPath(operation.NewUri) is { } newPath)
                paths.Add(Path.GetFullPath(newPath));
        }
        // Open editors are still backed by a real file.  Snapshot their disk bytes as well
        // as their in-memory text so an external write during the preview cannot be
        // overwritten by a later save.  DiskText is null for an open editor because the
        // editor buffer is the writer, but the file itself remains part of the transaction.
        foreach (var plan in plans)
            paths.Add(Path.GetFullPath(plan.Path));
        if (currentPreview is not null && currentPreview.Path.Length > 0)
            paths.Add(Path.GetFullPath(currentPreview.Path));

        var result = new Dictionary<string, LspFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (File.Exists(path))
                result[path] = new LspFileSnapshot(true, File.ReadAllBytes(path));
            else
                result[path] = new LspFileSnapshot(false, []);
        }
        return result;
    }

    private static Dictionary<string, LspFileSnapshot> CaptureLspFileSnapshots(
        IEnumerable<string> paths)
    {
        var result = new Dictionary<string, LspFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in paths)
        {
            var path = Path.GetFullPath(rawPath);
            result[path] = File.Exists(path)
                ? new LspFileSnapshot(true, File.ReadAllBytes(path))
                : new LspFileSnapshot(false, []);
        }
        return result;
    }

    private Dictionary<VimEditorControl, string> CaptureLspEditorSnapshots(
        IReadOnlyList<(string Path, IReadOnlyList<Editor.Core.Lsp.LspTextEdit> Edits, int? Version,
            List<VimEditorControl> Open, string OriginalText, string UpdatedText, string? DiskText,
            System.Text.Encoding? Encoding)> plans,
        WorkspaceEditPreviewFile? currentPreview)
    {
        var editors = plans.SelectMany(plan => plan.Open).ToHashSet();
        if (currentPreview is not null)
        {
            foreach (var tab in _editorTabs.Where(tab => tab.IsRealized &&
                EditorPathMatches(tab.Control, currentPreview.Path)))
                editors.Add(tab.Control);
        }
        return editors.ToDictionary(editor => editor, editor => editor.Text);
    }

    private static Dictionary<string, string> CaptureLspEditorTextSnapshots(
        IReadOnlyDictionary<VimEditorControl, string> editors,
        WorkspaceEditPreviewFile? currentPreview = null,
        bool useCurrentText = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (editor, text) in editors)
            if (editor.FilePath is { Length: > 0 } path)
                result[Path.GetFullPath(path)] = useCurrentText ? editor.Text : text;
        if (currentPreview is not null && currentPreview.Path.Length > 0)
            result[Path.GetFullPath(currentPreview.Path)] = currentPreview.UpdatedText;
        return result;
    }

    private static void VerifyLspTransactionSnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<VimEditorControl, string> editors)
    {
        foreach (var (path, expected) in files)
        {
            var actual = File.Exists(path)
                ? new LspFileSnapshot(true, File.ReadAllBytes(path))
                : new LspFileSnapshot(false, []);
            if (!SameLspFileSnapshot(expected, actual))
                throw new InvalidOperationException($"{path}: preview後に外部変更が検出されました。再度実行してください。");
        }
        foreach (var (editor, expected) in editors)
            if (!string.Equals(editor.Text, expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"{editor.FilePath}: preview後に編集中の内容が変更されました。再度実行してください。");
    }

    private static void RestoreLspTransactionSnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<VimEditorControl, string> editors)
    {
        foreach (var (editor, text) in editors)
#if LOOMO_EDITOR_HOST_API
            if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
                if (!editor.TryRestoreWorkspaceText(text, out var error))
                    throw new InvalidOperationException($"{editor.FilePath}: {error}");
#else
            if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
                throw new InvalidOperationException($"{editor.FilePath}: Editor package does not support workspace text restore.");
#endif

        foreach (var (path, snapshot) in files)
        {
            if (snapshot.Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, snapshot.Content);
            }
            else if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static bool SameLspFileSnapshot(LspFileSnapshot left, LspFileSnapshot right)
        => left.Exists == right.Exists && left.Content.AsSpan().SequenceEqual(right.Content);

    private void RecordWorkspaceEditHistory(
        string description,
        IReadOnlyDictionary<string, LspFileSnapshot> beforeFiles,
        IReadOnlyDictionary<string, LspFileSnapshot> afterFiles,
        IReadOnlyDictionary<string, string> beforeEditors,
        IReadOnlyDictionary<string, string> afterEditors)
    {
        if (beforeFiles.Count == 0 && beforeEditors.Count == 0)
            return;
        _workspaceEditUndo.Add(new WorkspaceEditHistoryEntry(
            description,
            new Dictionary<string, LspFileSnapshot>(beforeFiles, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, LspFileSnapshot>(afterFiles, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(beforeEditors, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(afterEditors, StringComparer.OrdinalIgnoreCase)));
        if (_workspaceEditUndo.Count > 50)
            _workspaceEditUndo.RemoveAt(0);
        _workspaceEditRedo.Clear();
    }

    private bool TryHandleWorkspaceEditUndo(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (_focusedRegion?.Pane != PaneKind.Editor ||
            e.Key != Key.Z ||
            !modifiers.HasFlag(ModifierKeys.Control) ||
            (modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            return false;

        var redo = modifiers.HasFlag(ModifierKeys.Shift);
        var history = redo ? _workspaceEditRedo : _workspaceEditUndo;
        if (history.Count == 0)
            return false;
        var entry = history[^1];
        var activePath = _activeEditorTab?.Control.FilePath;
        if (activePath is null ||
            !entry.AfterEditors.ContainsKey(Path.GetFullPath(activePath)))
            return false;
        bool stateMatches;
        try
        {
            stateMatches = WorkspaceEditStateMatches(
                redo ? entry.BeforeFiles : entry.AfterFiles,
                redo ? entry.BeforeEditors : entry.AfterEditors);
        }
        catch
        {
            return false;
        }
        if (!stateMatches)
            return false;
        try
        {
            _restoringWorkspaceEdit = true;
            RestoreLspTransactionSnapshots(
                redo ? entry.AfterFiles : entry.BeforeFiles,
                redo ? entry.AfterEditors : entry.BeforeEditors);
            history.RemoveAt(history.Count - 1);
            (redo ? _workspaceEditUndo : _workspaceEditRedo).Add(entry);
            EditorSharedStatusBar?.UpdateStatus(redo
                ? $"{entry.Description} をやり直しました。"
                : $"{entry.Description} を元に戻しました。");
            e.Handled = true;
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                // Undo/Redo中にディスクがロックされた場合も、先に戻せたEditorだけを
                // 残さないよう、元の状態へ再度戻す。
                RestoreLspTransactionSnapshots(
                    redo ? entry.BeforeFiles : entry.AfterFiles,
                    redo ? entry.BeforeEditors : entry.AfterEditors);
            }
            catch (Exception rollback)
            {
                EditorSharedStatusBar?.UpdateStatus(
                    $"WorkspaceEditの復元に失敗しました: {ex.Message} 復元にも失敗しました: {rollback.Message}");
                e.Handled = true;
                return true;
            }
            EditorSharedStatusBar?.UpdateStatus($"WorkspaceEditの復元に失敗しました: {ex.Message}");
            e.Handled = true;
            return true;
        }
        finally
        {
            _restoringWorkspaceEdit = false;
        }
    }

    private bool WorkspaceEditStateMatches(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<string, string> editors)
    {
        foreach (var (path, expected) in files)
        {
            var actual = File.Exists(path)
                ? new LspFileSnapshot(true, File.ReadAllBytes(path))
                : new LspFileSnapshot(false, []);
            if (!SameLspFileSnapshot(expected, actual))
                return false;
        }
        foreach (var (path, expected) in editors)
        {
            var open = _editorTabs.Where(tab => tab.IsRealized &&
                EditorPathMatches(tab.Control, path)).Select(tab => tab.Control).ToArray();
            if (open.Length == 0 || open.Any(editor => !string.Equals(editor.Text, expected, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private void RestoreLspTransactionSnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<string, string> editors)
    {
        foreach (var (path, text) in editors)
        {
            var open = _editorTabs.Where(tab => tab.IsRealized &&
                EditorPathMatches(tab.Control, path)).Select(tab => tab.Control).ToArray();
            if (open.Length == 0)
                throw new InvalidOperationException($"{path}: 対応するエディタタブが閉じられています。");
            foreach (var editor in open)
#if LOOMO_EDITOR_HOST_API
                if (!string.Equals(editor.Text, text, StringComparison.Ordinal) &&
                    !editor.TryRestoreWorkspaceText(text, out var error))
                    throw new InvalidOperationException($"{path}: {error}");
#else
                if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
                    throw new InvalidOperationException($"{path}: Editor package does not support workspace text restore.");
#endif
        }
        RestoreLspFileSnapshots(files);
    }

    private static void RestoreLspFileSnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files)
    {
        foreach (var (path, snapshot) in files)
        {
            if (snapshot.Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, snapshot.Content);
            }
            else if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void ValidateLspFileOperations(
        IReadOnlyList<Editor.Core.Lsp.LspFileOperation> operations,
        IReadOnlyList<string> folders)
    {
        foreach (var operation in operations) {
            var path = LspWorkspaceEditPaths.ResolveInWorkspace(operation.Uri, folders);
            switch (operation.Kind) {
                case Editor.Core.Lsp.LspFileOperationKind.Create:
                    if (File.Exists(path) && !operation.IgnoreIfExists && !operation.Overwrite)
                        throw new InvalidOperationException($"{path}: すでに存在します。");
                    break;
                case Editor.Core.Lsp.LspFileOperationKind.Rename:
                    if (!File.Exists(path))
                        throw new InvalidOperationException($"{path}: 名前変更元のファイルが見つかりません。");
                    var destination = LspWorkspaceEditPaths.ResolveInWorkspace(
                        operation.NewUri ?? throw new InvalidOperationException("改名先が指定されていません。"), folders);
                    if (File.Exists(destination) && !operation.IgnoreIfExists && !operation.Overwrite)
                        throw new InvalidOperationException($"{destination}: すでに存在します。");
                    break;
                case Editor.Core.Lsp.LspFileOperationKind.Delete:
                    if (!File.Exists(path) && !operation.IgnoreIfNotExists)
                        throw new InvalidOperationException($"{path}: 削除対象のファイルが見つかりません。");
                    break;
            }
        }
    }

    private static bool IsCreatedByOperation(
        string path, IReadOnlyList<Editor.Core.Lsp.LspFileOperation> operations)
        => operations.Any(operation => operation.Kind == Editor.Core.Lsp.LspFileOperationKind.Create &&
            string.Equals(LspUri.TryToLocalPath(operation.Uri), path, StringComparison.OrdinalIgnoreCase));

    private static string? FindRenameSource(
        string path, IReadOnlyList<Editor.Core.Lsp.LspFileOperation> operations)
    {
        var operation = operations.FirstOrDefault(candidate =>
            candidate.Kind == Editor.Core.Lsp.LspFileOperationKind.Rename &&
            string.Equals(LspUri.TryToLocalPath(candidate.NewUri ?? ""), path, StringComparison.OrdinalIgnoreCase));
        return operation is null ? null : LspUri.TryToLocalPath(operation.Uri);
    }

    private static WorkspaceEditPreviewOperation ToPreviewOperation(Editor.Core.Lsp.LspFileOperation operation)
        => new(
            operation.Kind switch {
                Editor.Core.Lsp.LspFileOperationKind.Create => "create",
                Editor.Core.Lsp.LspFileOperationKind.Rename => "rename",
                Editor.Core.Lsp.LspFileOperationKind.Delete => "delete",
                _ => "file operation",
            },
            LspUri.TryToLocalPath(operation.Uri) ?? operation.Uri,
            operation.NewUri is null ? null : LspUri.TryToLocalPath(operation.NewUri) ?? operation.NewUri);
    /// <summary>workspace edit のファイル操作（作成・改名・削除）。対象はワークスペース内に限る
    /// （<see cref="LspWorkspaceEditPaths.ResolveInWorkspace"/>）。</summary>
    private void ApplyLspFileOperations(
        IReadOnlyList<Editor.Core.Lsp.LspFileOperation> operations, IReadOnlyList<string> folders) {
        foreach (var operation in operations) {
            var path = LspWorkspaceEditPaths.ResolveInWorkspace(operation.Uri, folders);
            switch (operation.Kind) {
                case Editor.Core.Lsp.LspFileOperationKind.Create:
                    if (File.Exists(path)) {
                        if (operation.IgnoreIfExists) break;
                        if (!operation.Overwrite)
                            throw new InvalidOperationException($"{path}: すでに存在します。");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "");
                    break;
                case Editor.Core.Lsp.LspFileOperationKind.Rename:
                    var destination = LspWorkspaceEditPaths.ResolveInWorkspace(
                        operation.NewUri ?? throw new InvalidOperationException("改名先が指定されていません。"), folders);
                    if (File.Exists(destination) && !operation.Overwrite) {
                        if (operation.IgnoreIfExists) break;
                        throw new InvalidOperationException($"{destination}: すでに存在します。");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(path, destination, operation.Overwrite);
                    break;
                case Editor.Core.Lsp.LspFileOperationKind.Delete:
                    if (!File.Exists(path)) {
                        if (operation.IgnoreIfNotExists) break;
                        throw new InvalidOperationException($"{path}: 存在しません。");
                    }
                    File.Delete(path);
                    break;
            }
        }
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
        _appearance.ApplyUsingFoldingOnOpen(control);
        SyncEditorTestGlyphs(control);   // LoadFile はグリフを捨てるが BufferChanged を出さない
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
