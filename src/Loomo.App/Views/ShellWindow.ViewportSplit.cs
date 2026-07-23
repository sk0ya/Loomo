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
            newTab.Control.LoadFile(openPath);
        } else if (src is not null) {
            if (!string.IsNullOrWhiteSpace(src.Control.FilePath) && File.Exists(src.Control.FilePath) && !src.Control.IsModified)
                newTab.Control.LoadFile(src.Control.FilePath);
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
            tab.Pending = null;
        }
    }
    private readonly ConditionalWeakTable<VimEditorControl, StrongBox<IEditorLspManager?>> _editorLspManagers = new();
    private IEditorLspManager? GetLspManager(EditorTab tab) {
        if (!tab.IsRealized)
            return null; // コントロール未実体化＝LSP はまだ存在しない
        return _editorLspManagers.TryGetValue(tab.Control, out var box) ? box.Value : null;
    }
    private VimEditorControl BuildEditorControl(EditorTab tab) {
        var lspBox = new StrongBox<IEditorLspManager?>(null);
        var control = new VimEditorControl(new VimEditorControlOptions {
            GitServiceFactory = () => new GitDiffProvider(), LspManagerFactory = dispatcher => {
                var manager = new LspManager(dispatcher);
                lspBox.Value = manager;
                return manager;
            }
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
        control.MarkdownPreviewRequested += async (_, _) => await OpenEditorSupportAsync(tab);
        control.LinkClicked += OnEditorLinkClicked;
        control.FileLinkClicked += OnEditorFileLinkClicked;
        control.FindReferencesResult += OnEditorFindReferencesResult;
        control.ContextMenuBuilding += OnEditorContextMenuBuilding;
        control.BlameCommitClicked += (_, e) => ShowBlameCommitDiff(control, e.Blame);
        control.SplitRequested += (_, e) => SplitEditorView(e.Vertical ? SplitKind.Columns : SplitKind.Rows, e.FilePath);
        control.NewTabRequested += async (_, e) => await OpenEditorTabFromEditorAsync(e.FilePath);
        control.NextTabRequested += (_, _) => CycleEditorTab(+1);
        control.PrevTabRequested += (_, _) => CycleEditorTab(-1);
        control.CloseTabRequested += (_, _) => CloseActiveEditorTab();
        control.WindowCloseRequested += (_, _) => CloseEditorView();
        WireEditorForDebug(control);
        _editorLspManagers.AddOrUpdate(control, lspBox);
        return control;
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
        }
    }
    private void ApplyAppearanceToOpenTabs() {
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
