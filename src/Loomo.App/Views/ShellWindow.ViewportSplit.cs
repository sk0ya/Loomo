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
        var tab = new TerminalTab(requestedId ?? Guid.NewGuid(), view);
        view.HeaderTitleChanged += (_, title) => UpdateTerminalTab(tab, title);
        view.HyperlinkActivated += OnTerminalLinkActivated;
        view.ContextMenuBuilding += OnTerminalContextMenuBuilding;
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
            WorkspaceSessionCoordinator.RestoreEditor(control, snapshot);
            _appearance.ApplyUsingFoldingOnOpen(control);
            tab.Pending = null;
        }
    }
    /// <summary>このタブが今持っている LSP 文書ハンドル。未実体化・対応サーバー無しなら null。
    /// 文書スコープの問い合わせ（アウトライン・参照）はこれを使い、ワークスペーススコープは
    /// <c>_lspWorkspace</c> へ直接投げる。</summary>
    private ILspDocument? GetLspDocument(EditorTab tab) =>
        tab.IsRealized ? tab.Control.LspDocument : null;
    private VimEditorControl BuildEditorControl(EditorTab tab) {
        var control = new VimEditorControl(new VimEditorControlOptions {
            GitServiceFactory = () => new GitDiffProvider(),
            // ワークスペースフォルダーも文書の参照カウントもサーバーのプールもワークスペース側が知っている。
            LspWorkspace = _lspWorkspace, LspServerAdmin = _lspServerAdmin,
            EngineServices = _editorEngineServices,
            // 「名前の変更」は Loomo の「リファクタリング」サブメニューに入れる（§32）。
            // これを渡さないとコントロール側の "Rename Symbol" と2つ並ぶ。
            HostProvidesRenameMenuItem = true
        }) {
            VimEnabled = _settings.Vim.Enabled, Visibility = Visibility.Collapsed
        };
        _appearance.ApplyEditorOptions(control);
        _appearance.ApplyEditorAppearance(control);
        control.SetSharedStatusBar(EditorSharedStatusBar);
        control.BufferChanged += (_, _) => {
            UpdateEditorTab(tab);
            RecordTrailEdit(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                ScheduleEditorSupportUpdate();
        };
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
        control.OpenFileRequested += (_, e) => _ = OpenPathInEditorAsync(
            e.FilePath,
            e.Line > 0 ? e.Line + 1 : 0,
            e.Column > 0 ? e.Column + 1 : 0);
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
        return control;
    }
    private void OnEditorWorkspaceEditRequested(object? sender, WorkspaceEditRequestedEventArgs e) {
        e.Error = ApplyLspWorkspaceEdit(e.Changes, e.DocumentVersions, e.FileOperations);
        e.Handled = e.Error is null;
    }
    /// <summary>workspace edit をワークスペースへ適用する。成功なら null、失敗ならユーザーへ出す文言を返す。
    /// 検証は全ファイルぶん先に済ませてから書き込む（途中まで適用された壊れた状態を作らない）。
    /// ファイル操作は本文の編集より**先**に行う——新規作成されたファイルへの編集が続くため
    /// （「クラスに抽出」「型をファイルへ移動」がこの形）。</summary>
    private string? ApplyLspWorkspaceEdit(
        IReadOnlyDictionary<string, IReadOnlyList<Editor.Core.Lsp.LspTextEdit>> changes,
        IReadOnlyDictionary<string, int?>? documentVersions,
        IReadOnlyList<Editor.Core.Lsp.LspFileOperation>? fileOperations) {
        // マルチルート（プライマリ＋追加フォルダー）の全件で判定する。プライマリだけを見ていた頃は、
        // あとから追加したフォルダーのファイルが「ワークスペース外」になり編集ごと失敗していた。
        // 正本は _workspace.Folders——LSP のサーバー自身もこの一覧で initialize されている。
        var folders = _workspace.Folders;
        if (folders.Count == 0)
            return "ワークスペースが開かれていません。";
        try {
            if (fileOperations is { Count: > 0 })
                ApplyLspFileOperations(fileOperations, folders);
            var plans = new List<(string Path, IReadOnlyList<Editor.Core.Lsp.LspTextEdit> Edits,
                int? Version, List<VimEditorControl> Open, string? DiskText, System.Text.Encoding? Encoding)>();
            foreach (var (uri, edits) in changes) {
                var path = LspWorkspaceEditPaths.ResolveInWorkspace(uri, folders);

                int? expectedVersion = null;
                documentVersions?.TryGetValue(uri, out expectedVersion);
                var open = _editorTabs
                    .Where(tab => tab.IsRealized && string.Equals(Path.GetFullPath(tab.Control.FilePath ?? ""), path, StringComparison.OrdinalIgnoreCase))
                    .Select(tab => tab.Control)
                    .ToList();
                if (open.Count > 0) {
                    foreach (var editor in open) {
                        if (expectedVersion is not null && editor.LspDocument?.Version is { } actual && actual != expectedVersion)
                            throw new InvalidOperationException($"{path}: 文書版が一致しません（要求 {expectedVersion} / 現在 {actual}）。");
                        _ = VimEditorControl.ApplyTextEdits(editor.Text, edits); // 全文書を先に検証し、途中適用を避ける
                    }
                    plans.Add((path, edits, expectedVersion, open, null, null));
                    continue;
                }
                if (expectedVersion is not null)
                    throw new InvalidOperationException($"{path}: 文書版 {expectedVersion} を検証できません。ファイルを開いて再度実行してください。");
                string original;
                System.Text.Encoding encoding;
                using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true)) {
                    original = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                var updated = VimEditorControl.ApplyTextEdits(original, edits);
                plans.Add((path, edits, null, [], updated, encoding));
            }
            foreach (var plan in plans) {
                // 読み取り側を先に同期し、LSPへdidChangeを送るwriterは最後に1回だけ適用する。
                foreach (var editor in plan.Open.OrderBy(editor => editor.LspDocument?.IsWriter == true))
                    if (!editor.TryApplyLspTextEdits(plan.Edits, expectedVersion: null, out var error))
                        throw new InvalidOperationException($"{plan.Path}: {error}");
                if (plan.DiskText is not null)
                    File.WriteAllText(plan.Path, plan.DiskText, plan.Encoding!);
            }
            return null;
        }
        catch (Exception ex) {
            return ex.Message;
        }
    }
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
    private void LoadEditorFile(VimEditorControl control, string path) {
        control.LoadFile(path);
        _appearance.ApplyUsingFoldingOnOpen(control);
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
