namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: コマンドパレット（部屋全体の操作統一）。移動・ペイン表示・タブ・コンポーザ・ ペグボード・サイドバー・ワークスペース切替といった既存操作に名前を付け、 Ctrl+Shift+P（または Ctrl+W p）から検索して実行できるようにする。 一覧は開くたびに現在状態（ステージ中か・WS一覧など）から組み直す。 絞り込みロジックは <see cref="PaletteFilter"/>（純ロジック・テスト済み）。</summary>
public partial class ShellWindow {
    private IReadOnlyList<PaletteCommand> _paletteCommands = Array.Empty<PaletteCommand>();
    private bool IsPaletteOpen => CommandPaletteOverlay.Visibility == Visibility.Visible;
    private void OpenCommandPalette() {
        _paletteCommands = BuildPaletteCommands();
        _paletteView.RefreshAppearance();
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        UpdatePaletteBoxSize();
        PaletteInput.Text = string.Empty;
        RefilterPalette();
        PaletteInput.Focus();
    }
    private bool _palettePreviewShown;
    private void UpdatePaletteBoxSize() {
        _paletteView.UpdateSize(ActualWidth, ActualHeight, _palettePreviewShown);
    }
    private void OnPaletteOverlaySizeChanged(object sender, SizeChangedEventArgs e) {
        if (IsPaletteOpen)
            UpdatePaletteBoxSize();
    }
    private void CloseCommandPalette(bool refocus) {
        if (!IsPaletteOpen)
            return;
        _paletteSearch.Cancel();
        _paletteView.Cancel();
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }
    private void SetPaletteMode(PaletteMode mode) {
        _paletteView.SetMode(mode);
    }
    private void CyclePaletteMode() {
        _paletteView.CycleMode();
    }
    private void OnPaletteModeClick(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaletteMode>(tag, out var mode))
            SetPaletteMode(mode);
    }
    private void UpdateModeChips(PaletteMode mode) {
        _paletteView.UpdateMode(mode);
    }
    private void RefilterPalette() {
        var (mode, query) = CommandPaletteService.Parse(PaletteInput.Text);
        UpdateModeChips(mode);
        var showPreview = mode is PaletteMode.File or PaletteMode.Grep or PaletteMode.Class or PaletteMode.Symbol
            || (mode == PaletteMode.All && !string.IsNullOrWhiteSpace(query));
        _paletteView.SetPreviewVisible(showPreview);
        _palettePreviewShown = showPreview;
        UpdatePaletteBoxSize();
        if (mode == PaletteMode.Command) {
            _paletteSearch.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, query));
            return;
        }
        if (mode == PaletteMode.Terminal) {
            _paletteSearch.Cancel();
            ShowPaletteItems(PaletteSearchCoordinator.TerminalMatches(
                _activeTerminalTab?.View, query, (match, view) => {
                    EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
                    view.SelectMatch(match);
                    view.FocusTerminal();
                }));
            return;
        }
        if (mode == PaletteMode.All && string.IsNullOrWhiteSpace(query)) {
            _paletteSearch.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, string.Empty));
            return;
        }
        _ = RefilterSearchAsync(mode, query);
    }
    private async Task RefilterSearchAsync(PaletteMode mode, string query) {
        var items = await _paletteSearch.SearchLatestAsync(mode, query, _paletteCommands,
            () => PaletteSearchCoordinator.ConnectedCodeManagers(
                _activeEditorTab, _editorTabs, _codeSupport, GetLspManager),
            FileEntry, GrepEntry, SymbolEntry);
        if (items is not null)
            ShowPaletteItems(items);
    }
    private PaletteCommand SymbolEntry(LspSymbolInformation sym, string category) {
        var path = CodeEditorSupport.TryUriToLocalPath(sym.Location?.Uri);
        var line1 = (sym.Location?.Range?.Start?.Line ?? 0) + 1;
        var title = string.IsNullOrEmpty(sym.ContainerName) ? sym.Name : $"{sym.Name}  ·  {sym.ContainerName}";
        return new PaletteCommand(category, title, () => { if (path is not null) _ = OpenAndNavigateAsync(path, line1); }) {
            PreviewPath = path, PreviewLine = line1, };
    }
    private void ShowPaletteItems(IReadOnlyList<PaletteCommand> items) {
        var (_, query) = CommandPaletteService.Parse(PaletteInput.Text);
        _paletteView.ShowItems(items, query);
    }
    private PaletteCommand FileEntry(FileSearchHit hit)
        => new("ファイル", hit.RelativePath, () => _ = OpenAndNavigateAsync(hit.FullPath, 0))
        { PreviewPath = hit.FullPath };
    private PaletteCommand GrepEntry(ContentSearchHit hit, string query)
        => new($"{hit.RelativePath}:{hit.Line}", hit.LineText.Trim(), () => _ = OpenAndNavigateAsync(hit.FullPath, hit.Line))
        { PreviewPath = hit.FullPath, PreviewLine = hit.Line, PreviewHighlight = query };
    private async Task OpenAndNavigateAsync(string path, int line) {
        await OpenFileInNewEditorTabAsync(path);
        if (line > 0 && _activeEditorTab?.Control is { } control)
            control.NavigateTo(line - 1, 0);
    }
    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _paletteView.UpdatePreview(PaletteList.SelectedItem as PaletteCommand);
    private void ExecutePaletteSelection() {
        if (PaletteList.SelectedItem is not PaletteCommand command)
            return;
        CloseCommandPalette(refocus: false);
        command.Execute();
    }
    private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => RefilterPalette();
    private void OnPaletteInputKeyDown(object sender, KeyEventArgs e) {
        switch (e.Key) {
            case Key.Escape:
                CloseCommandPalette(refocus: true);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecutePaletteSelection();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                MovePaletteSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
    }
    private void MovePaletteSelection(int delta) {
        _paletteView.MoveSelection(delta);
    }
    private void OnPaletteBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        => CloseCommandPalette(refocus: true);
    private void OnPaletteBoxMouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;
    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e) {
        if (sender is ListBoxItem { DataContext: PaletteCommand command }) {
            e.Handled = true;
            CloseCommandPalette(refocus: false);
            command.Execute();
        }
    }
    private List<PaletteCommand> BuildPaletteCommands() {
        var list = new List<PaletteCommand>();
        string? Sc(string id) => _keybindings.For(id)?.Format();
        list.Add(new("ステージ", _stageActive ? "ステージモードを解除（タイル表示へ）" : "ステージモードへ（舞台＋袖）", () => { if (_stageActive) ExitStageMode(); else EnterStageMode(); }));
        if (_stageActive)
            list.Add(new("ステージ", _overviewActive ? "俯瞰を閉じる" : "俯瞰（全カードを一望）", ToggleOverview, "Ctrl+W z"));
        foreach (var kind in StageOrder) {
            var target = kind;
            list.Add(new("移動", $"{PaneLabel(target)} へ", () => { SetPaneVisible(target, true); FocusPane(target); }));
        }
        foreach (var kind in StageOrder) {
            var target = kind;
            list.Add(new("ペイン", $"{PaneLabel(target)} の表示を切替", () => SetPaneVisible(target, !IsPaneVisible(target))));
        }
        list.Add(new("タブ", "新しいターミナルタブ", () => OnTerminalNewTab(this, new RoutedEventArgs()), Sc("tab.newTerminal"), "tab.newTerminal"));
        list.Add(new("タブ", "新しいエディタタブ", () => OnEditorNewTab(this, new RoutedEventArgs()), Sc("tab.newEditor"), "tab.newEditor"));
        list.Add(new("タブ", "新しいブラウザタブ", () => OnBrowserNewTab(this, new RoutedEventArgs()), Sc("tab.newBrowser"), "tab.newBrowser"));
        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く", () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, Sc("composer.run"), "composer.run"));
        list.Add(new("コンポーザ", "本文をペグボードへピン", () => OnComposerPinToPegboard(this, new RoutedEventArgs())));
        list.Add(new("ペグボード", "クリップボードから追加", () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択をピン", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLをピン", PinBrowserUrlToPegboard));
        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null), Sc("sidebar.explorer"), "sidebar.explorer"));
        list.Add(new("サイドバー", "検索（全文検索 / grep）", () => _vm.ShowSearchCommand.Execute(null), Sc("sidebar.search"), "sidebar.search"));
        list.Add(new("サイドバー", "タブ一覧", () => _vm.ShowTabsCommand.Execute(null), Sc("sidebar.tabs"), "sidebar.tabs"));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null), Sc("sidebar.git"), "sidebar.git"));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null), Sc("sidebar.pegboard"), "sidebar.pegboard"));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null), Sc("sidebar.settings"), "sidebar.settings"));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null), Sc("sidebar.appearance"), "sidebar.appearance"));
        list.Add(new("サイドバー", "キーボード設定", () => _vm.ShowKeyboardSettingsCommand.Execute(null)));
        list.Add(new("サイドバー", "エクスプローラで現在のファイルを選択（同期）", RevealActiveFileInFolderTree, Sc("explorer.revealActiveFile"), "explorer.revealActiveFile"));
        list.Add(new("AI", "AIセッション一覧を開閉", () => _vm.Sessions.ToggleOpenCommand.Execute(null), Sc("sidebar.sessions"), "sidebar.sessions"));
        foreach (var workspace in _vm.Workspaces.Workspaces.Where(w => !w.IsActive)) {
            var target = workspace;
            list.Add(new("ワークスペース", $"切替: {target.Name}", () => _vm.Workspaces.ActivateWorkspaceCommand.Execute(target)));
        }
        return list;
    }
}
