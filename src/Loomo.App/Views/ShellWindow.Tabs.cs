using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナル／エディタのタブ管理（作成・選択・クローズ・プレビュータブ）</summary>
public partial class ShellWindow {
    private void OnTerminalNewTab(object sender, RoutedEventArgs e) {
        var startDir = _activeTerminalTab?.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
            startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
        var tab = CreateTerminalTab(startDir);
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);
        ActivateTerminalTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }
    private void OnTerminalTabSelected(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateTerminalTab(id);
    }
    private async void OnTabMiddleClick(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { Tag: Guid id })
            return;
        e.Handled = true;
        if (_terminalTabs.Any(t => t.Id == id))
            await CloseTerminalTabAsync(id);
        else if (_editorTabs.Any(t => t.Id == id))
            CloseEditorTab(id);
        else if (_browserTabs.Any(t => t.Id == id))
            await CloseBrowserTabAsync(id);
        else
            return;
        SaveActiveWorkspaceSnapshot();
    }
    private async void OnTerminalTabClosed(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id }) {
            await CloseTerminalTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }
    private void OnEditorTabSelected(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateEditorTab(id);
    }
    private TerminalWorkspaceTabs CurrentTerminalWorkspace
        => _activeTerminalWorkspace ?? _scratchTerminalWorkspace;
    private EditorWorkspaceTabs CurrentEditorWorkspace
        => _activeEditorWorkspace ?? _scratchEditorWorkspace;
    private void ActivateTerminalTab(
        Guid id, WorkspaceSwitchProfiler? profile = null, bool focusView = true) {
        var tab = _terminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;
        _terminalViews?.Activate(id, focusView);
        profile?.Lap("terminal.views");
        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = id;
        _terminal.Attach(tab.View);
        profile?.Lap("terminal.serviceAttach");
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        profile?.Lap("terminal.cwd");
        _vm.Tabs.ActivateTerminalTab(id);
        profile?.Lap("terminal.tabVm");
        RecordTrailTerminalTab(tab);
        SaveActiveWorkspaceSnapshot();
        profile?.Lap("terminal.bookkeeping");
    }
    private void SetActiveTerminalTab(TerminalTab tab) {
        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = tab.Id;
        _terminal.Attach(tab.View);
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        _vm.Tabs.ActivateTerminalTab(tab.Id);
        RecordTrailTerminalTab(tab);
    }
    private async Task CloseTerminalTabAsync(Guid id) {
        var index = _terminalTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;
        var wasActive = _activeTerminalTab?.Id == id;
        var tab = _terminalTabs[index];
        ViewportTree.Detach(tab.View);
        await tab.View.CloseAsync();
        _terminalTabs.RemoveAt(index);
        _vm.Tabs.RemoveTerminalTab(id);
        _terminalViews?.RemoveTab(id);
        ForgetTerminalActivity(id);
        if (_terminalTabs.Count == 0) {
            var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
            var newTab = CreateTerminalTab(startDir);
            _terminalTabs.Add(newTab);
            _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
            ActivateTerminalTab(newTab.Id);
            return;
        }
        _terminalViews?.RepairTabs(_terminalTabs.Select(t => t.Id));
        if (wasActive) {
            ActivateTerminalTab(_terminalTabs[Math.Min(index, _terminalTabs.Count - 1)].Id);
        } else {
            _terminalViews?.Rebuild();
            if (_terminalViews?.FocusedTabId is { } fid && _terminalTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                SetActiveTerminalTab(ft);
        }
    }
    private void ActivateEditorTab(
        Guid id, WorkspaceSwitchProfiler? profile = null, bool focusView = true) {
        var tab = _editorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;
        _editorViews?.Activate(id, focusView);
        profile?.Lap("editor.views");
        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = id;
        _editor.Attach(tab.Control);
        profile?.Lap("editor.serviceAttach");
        _vm.Tabs.ActivateEditorTab(id);
        profile?.Lap("editor.tabVm");
        QueueEditorTabHeaderIntoView(id);
        SyncEditorStatusBar(tab);
        SwitchEditorSupportSource(tab);
        profile?.Lap("editor.support");
        RecordTrailEditorTab(tab);
        OnActiveEditorFileChanged(tab);
        SaveActiveWorkspaceSnapshot();
        profile?.Lap("editor.bookkeeping");
    }
    private void SetActiveEditorTab(EditorTab tab) {
        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = tab.Id;
        _editor.Attach(tab.Control);
        _vm.Tabs.ActivateEditorTab(tab.Id);
        QueueEditorTabHeaderIntoView(tab.Id);
        SyncEditorStatusBar(tab);
        SwitchEditorSupportSource(tab);
        RecordTrailEditorTab(tab);
        OnActiveEditorFileChanged(tab);
    }
    /// <summary>
    /// 分割しても下端に1つだけ出す共有ステータスバー（<c>EditorSharedStatusBar</c>）を、いま活性化した
    /// エディタの状態（ファイル名・行数・モード・カーソル）へ合わせる。
    /// <para>
    /// バーは全エディタで共有され、内容は<b>どのコントロールも自分の都合で</b>書き込む
    /// （<c>VimEditorControl.SyncStatusBar</c> に「自分が現在のエディタか」の判定は無い）。
    /// エディタ側が自発的に押し出すのはキーボードフォーカス取得時だけなので、
    /// <b>サイドバーのタブ一覧をクリックして切り替えると</b>——フォーカスは一覧に残るので——誰も押し出さず、
    /// 前のタブのファイル名・行数が残ったままになる（1つ遅れ）。
    /// </para>
    /// <para>
    /// さらに、活性化の最中は再ペアレント・レイアウト・実体化が走り、その過程で<b>裏になった
    /// コントロール</b>が <c>UpdateAll</c> 経由でバーを書き戻す。そのため活性化直後に1回押すだけでは
    /// 上書きされる（実測）。落ち着いてから（Background 優先度）もう一度、<b>その時点でもまだ
    /// アクティブなら</b>押し直す。
    /// </para>
    /// <para>
    /// 呼び出しは <see cref="ActivateEditorTab"/> と <see cref="SetActiveEditorTab"/> の両方に要る
    /// （活性化の経路が2本あり、片方にしか無いと同じ「1つ遅れ」が別経路で復活する。設計書 §30.6 P3 と同じ轍）。
    /// </para>
    /// <para>
    /// 本来の直し方は Editor パッケージ側で「共有バーの持ち主」を持たせ、持ち主以外の書き込みを
    /// 届かせないこと。ここはホスト側から押し戻しているだけなので、活性化と無関係な非同期処理
    /// （裏タブの外部変更検知など）が後から書き戻す余地は残る。
    /// </para>
    /// </summary>
    private void SyncEditorStatusBar(EditorTab tab) {
        if (!tab.IsRealized)
            return;
        tab.Control.SyncStatusBar();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
            if (ReferenceEquals(_activeEditorTab, tab) && tab.IsRealized)
                tab.Control.SyncStatusBar();
        }));
    }
    // アクティブなエディタが指すファイルが変わったときの唯一の評価点。
    // 以前は ActivateEditorTab / SetActiveEditorTab の二本立てで後者にしか評価が無く、しかも
    // 新規タブは Activate→LoadFile の順なので評価時点でパスが未設定だった（設計書 §30.6 P3）。
    // LoadFile の後にもう一度呼べるよう、同じパスの二度目は捨てる。
    private string? _lastLspPromptPath;
    private LspPromptInfo? _lastLspPrompt;
    private void OnActiveEditorFileChanged(EditorTab tab) {
        var filePath = tab.IsRealized ? tab.Control.FilePath : tab.PeekFilePath;
        _vm.Debug.Problems.CurrentFilePath = filePath;
        _vm.TsIde.Problems.CurrentFilePath = filePath;
        // 読み込みを伴わないタブ活性化ぶんの再送（読み込みを伴う経路は LoadEditorFile が受け持つ）。
        // 同じ内容なら Editor 側が no-op にするので、重複して送っても害はない。
        if (tab.IsRealized) SyncEditorTestGlyphs(tab.Control);
        if (string.Equals(filePath, _lastLspPromptPath, StringComparison.OrdinalIgnoreCase))
            return;
        _lastLspPromptPath = filePath;
        _lastLspPrompt = _lspManagement.EvaluateForFile(filePath);
        _vm.LspPrompt.Show(_lastLspPrompt);
    }
    /// <summary>促し判定の使い回し（アウトラインの案内も同じ結果を出すため）。
    /// 「今後表示しない／今回は閉じた」の抑止は毎回 <see cref="LspPromptViewModel.Filter"/> を通して適用する
    /// （キャッシュは素の判定結果なので、バーを閉じた直後の再評価でも抑止が効くようにするため）。</summary>
    private LspPromptInfo? EvaluateLspPrompt(string? filePath) =>
        _vm.LspPrompt.Filter(
            string.Equals(filePath, _lastLspPromptPath, StringComparison.OrdinalIgnoreCase)
                ? _lastLspPrompt
                : _lspManagement.EvaluateForFile(filePath));
    /// <summary>このファイルの言語サーバーが起動・初期化に失敗しているか（案内に理由を出すため）。
    /// 促し（未導入／未設定）は <see cref="EvaluateLspPrompt"/> の担当で、こちらは
    /// 「導入も設定も済んでいるのに繋がらない」場合だけを見る。状態の出所は LSP セッション
    /// （<c>LspWorkspaceService.ServerStatuses</c>）— 促し判定は対応表と PATH しか見ないので判らない。</summary>
    private LspServerFailure? EvaluateLspFailure(string? filePath) {
        if (string.IsNullOrEmpty(filePath))
            return null;
        var ext = LspExtensions.NormalizeExt(Path.GetExtension(filePath));
        if (ext.Length == 0 || _lspManagement.ResolveServerFor(ext) is not { } server)
            return null;
        return LspNoticeModel.FindFailure(
            _lspWorkspace.ServerStatuses, ext, server.Executable, server.DisplayName);
    }
    private void QueueEditorTabHeaderIntoView(Guid id) {
        Dispatcher.BeginInvoke( new Action(() => ScrollEditorTabHeaderIntoView(id)), DispatcherPriority.Loaded);
    }
    private void ScrollEditorTabHeaderIntoView(Guid id) {
        if (EditorTabStripScrollViewer.ViewportWidth <= 0)
            return;
        EditorTabStripItems.UpdateLayout();
        if (FindEditorTabHeader(id, EditorTabStripItems) is not { } header)
            return;
        var bounds = header.TransformToAncestor(EditorTabStripScrollViewer)
            .TransformBounds(new Rect(0, 0, header.ActualWidth, header.ActualHeight));
        if (bounds.Left < 0) {
            EditorTabStripScrollViewer.ScrollToHorizontalOffset( Math.Max(0, EditorTabStripScrollViewer.HorizontalOffset + bounds.Left));
        } else if (bounds.Right > EditorTabStripScrollViewer.ViewportWidth) {
            EditorTabStripScrollViewer.ScrollToHorizontalOffset( EditorTabStripScrollViewer.HorizontalOffset + bounds.Right - EditorTabStripScrollViewer.ViewportWidth);
        }
    }
    private static FrameworkElement? FindEditorTabHeader(Guid id, DependencyObject root) {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { DataContext: TabEntryViewModel tab } element && tab.Id == id)
                return element;
            if (FindEditorTabHeader(id, child) is { } found)
                return found;
        }
        return null;
    }
    private void CloseEditorTab(Guid id) {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;
        var wasActive = _activeEditorTab?.Id == id;
        var tab = _editorTabs[index];
        if (ReferenceEquals(_editorSupport.Source, tab)) {
            _editorSupportDebounceTimer?.Stop();
            DetachEditorSupportSource();
            _editorSupport.IsPinned = false;
            UpdateEditorSupportPinToggle();
        }
        if (tab.IsRealized) {
            ViewportTree.Detach(tab.Control);
            tab.Control.Dispose();
        }
        if (ReferenceEquals(_previewEditorTab, tab))
            _previewEditorTab = null;
        if (tab.PeekFilePath is { Length: > 0 } closedPath) {
            _editorSupport.History.Remove(closedPath);
            UpdateEditorSupportNavAffordances();
        }
        _editorTabs.RemoveAt(index);
        _vm.Tabs.RemoveEditorTab(id);
        _editorViews?.RemoveTab(id);
        if (_editorTabs.Count == 0) {
            var newTab = CreateEditorTab();
            _editorTabs.Add(newTab);
            _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
            ActivateEditorTab(newTab.Id);
            return;
        }
        _editorViews?.RepairTabs(_editorTabs.Select(t => t.Id));
        if (wasActive) {
            ActivateEditorTab(_editorTabs[Math.Min(index, _editorTabs.Count - 1)].Id);
        } else {
            _editorViews?.Rebuild();
            if (_editorViews?.FocusedTabId is { } fid && _editorTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                SetActiveEditorTab(ft);
        }
    }
    private void OnFolderTreeEntryRenamed(EntryRenamedEventArgs e) {
        if (e.IsDirectory)
            _vm.Recent.RecordFolder(e.NewPath);
        else
            _vm.Recent.RecordFile(e.NewPath);
        foreach (var tab in _editorTabs) {
            var path = tab.PeekFilePath;
            if (string.IsNullOrEmpty(path))
                continue;
            string? newPath = null;
            if (e.IsDirectory) {
                if (IsPathUnder(path, e.OldPath))
                    newPath = Path.GetFullPath(Path.Combine(e.NewPath, Path.GetRelativePath(e.OldPath, path)));
            } else if (PathsEqual(path, e.OldPath)) {
                newPath = e.NewPath;
            }
            if (newPath is not null)
                RebaseEditorTabPath(tab, newPath);
        }
    }
    /// <summary>
    /// 開いたままのタブを新しいパスへ付け替える。<b>バッファの中身（未保存の編集も）はそのまま</b>で、
    /// 名乗るパスだけが変わる。
    /// <para>
    /// バッファの <c>FilePath</c> を直接書くだけでは足りない。拡張子から決まっているもの——
    /// シンタックスハイライトの言語、LSP のドキュメント URI（＝担当サーバーごと変わる）、
    /// 外部変更を見るウォッチャ、相棒ペイン（EditorSupport）の種類——が旧拡張子のまま固まる。
    /// 言語判定はコントロール内部なのでホストからは触れず、<c>RebaseFilePath</c>（Editor 1.0.78）に任せる。
    /// </para>
    /// </summary>
    private void RebaseEditorTabPath(EditorTab tab, string newPath) {
        if (tab.IsRealized) {
            tab.Control.RebaseFilePath(newPath);
            UpdateEditorTab(tab);   // タブ名更新＋スナップショット保存
            // 相棒ペインの種類（md プレビュー／CSV 表／コードのアウトライン）は毎回の描画で
            // パスから決め直すので、追従元が自分なら描き直しを1回頼めばよい。
            if (ReferenceEquals(_editorSupport.Source, tab))
                InvalidateEditorSupport();
            if (ReferenceEquals(_activeEditorTab, tab))
                OnActiveEditorFileChanged(tab);   // LSP の促し・問題一覧の対象ファイル
        } else if (tab.Pending is { } pending) {
            pending.FilePath = newPath;
            pending.Title = Path.GetFileName(newPath);
            _vm.Tabs.UpdateEditorTab(tab.Id, newPath, pending.IsModified);
            SaveActiveWorkspaceSnapshot();
        }
    }
    private void OnFolderTreeEntryDeleted(string deletedPath) {
        var affected = _editorTabs
            .Where(t => t.PeekFilePath is { Length: > 0 } p
                && (PathsEqual(p, deletedPath) || IsPathUnder(p, deletedPath)))
            .Select(t => t.Id)
            .ToList();
        if (affected.Count == 0)
            return;
        foreach (var id in affected)
            CloseEditorTab(id);
        SaveActiveWorkspaceSnapshot();
    }
    // 正規化はワークスペース側の 1 本（WorkspacePaths.Normalize）に寄せる——同じ形の GetFullPath+TrimEnd を
    // 各所で書き直すと「片方だけ正規化していて一致しない」が起きる。
    private static bool PathsEqual(string a, string b)
        => string.Equals(WorkspacePaths.Normalize(a), WorkspacePaths.Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static bool IsPathUnder(string path, string directory) {
        var dir = Path.GetFullPath(directory).TrimEnd('\\', '/');
        var full = Path.GetFullPath(path);
        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private void RevealActiveFileInFolderTree() {
        var path = _activeEditorTab?.PeekFilePath;
        if (string.IsNullOrEmpty(path))
            return;
        _vm.RevealExplorerPanel();
        // ツリーは SidebarContainer の直下ではなく ExplorerSection（タブ一覧と分割した行）の中に
        // 入っているので、直下の子を探すと必ず null になって同期が黙って効かなくなる。名前で持つ。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => SidebarFolderTree.RevealPath(path)));
    }
}
