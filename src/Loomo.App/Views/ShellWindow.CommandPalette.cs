
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: コマンドパレット（部屋全体の操作統一）。移動・ペイン表示・タブ・コンポーザ・
/// ペグボード・サイドバー・ワークスペース切替といった既存操作に名前を付け、
/// Ctrl+Shift+P（または Ctrl+W p）から検索して実行できるようにする。
/// 一覧は開くたびに現在状態（ステージ中か・WS一覧など）から組み直す。
/// 絞り込みロジックは <see cref="PaletteFilter"/>（純ロジック・テスト済み）。
/// </summary>
public partial class ShellWindow
{
    private IReadOnlyList<PaletteCommand> _paletteCommands = Array.Empty<PaletteCommand>();

    private bool IsPaletteOpen => CommandPaletteOverlay.Visibility == Visibility.Visible;

    private void OpenCommandPalette()
    {
        _paletteCommands = BuildPaletteCommands();
        if (_previewEditor is not null)
            _appearance.ApplyEditorAppearance(_previewEditor);
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        UpdatePaletteBoxSize();
        PaletteInput.Text = string.Empty;
        RefilterPalette();
        PaletteInput.Focus();
    }

    private bool _palettePreviewShown;

    private void UpdatePaletteBoxSize()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        PaletteBox.Width = Math.Clamp(w * 0.72, 760, 1600);
        var tall = Math.Max(440, h * 0.82);
        PaletteBox.MaxHeight = tall;
        PaletteBox.Height = _palettePreviewShown ? tall : double.NaN;
    }

    private void OnPaletteOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsPaletteOpen)
            UpdatePaletteBoxSize();
    }

    private void CloseCommandPalette(bool refocus)
    {
        if (!IsPaletteOpen)
            return;
        _paletteSearch.Cancel();
        _palettePreviewCts?.Cancel();
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }

    private void SetPaletteMode(PaletteMode mode)
    {
        var (_, query) = CommandPaletteService.Parse(PaletteInput.Text);
        PaletteInput.Text = CommandPaletteService.Prefix(mode) + query;     // TextChanged が RefilterPalette を呼ぶ
        PaletteInput.CaretIndex = PaletteInput.Text.Length;
        PaletteInput.Focus();
    }

    private void CyclePaletteMode()
    {
        var (mode, _) = CommandPaletteService.Parse(PaletteInput.Text);
        SetPaletteMode(CommandPaletteService.Next(mode));
    }

    private void OnPaletteModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaletteMode>(tag, out var mode))
            SetPaletteMode(mode);
    }

    private void UpdateModeChips(PaletteMode mode)
    {
        Highlight(PaletteModeAll, mode == PaletteMode.All);
        Highlight(PaletteModeFile, mode == PaletteMode.File);
        Highlight(PaletteModeGrep, mode == PaletteMode.Grep);
        Highlight(PaletteModeClass, mode == PaletteMode.Class);
        Highlight(PaletteModeSymbol, mode == PaletteMode.Symbol);
        Highlight(PaletteModeTerminal, mode == PaletteMode.Terminal);
        Highlight(PaletteModeCommand, mode == PaletteMode.Command);

        static void Highlight(Button chip, bool active)
        {
            if (active)
            {
                chip.SetResourceReference(Control.BorderBrushProperty, "Accent");
                chip.SetResourceReference(Control.ForegroundProperty, "Fg");
            }
            else
            {
                chip.BorderBrush = System.Windows.Media.Brushes.Transparent;
                chip.SetResourceReference(Control.ForegroundProperty, "FgDim");
            }
        }
    }

    private void RefilterPalette()
    {
        var (mode, query) = CommandPaletteService.Parse(PaletteInput.Text);
        UpdateModeChips(mode);

        var showPreview = mode is PaletteMode.File or PaletteMode.Grep or PaletteMode.Class or PaletteMode.Symbol
            || (mode == PaletteMode.All && !string.IsNullOrWhiteSpace(query));
        PalettePreviewColumn.Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _palettePreviewShown = showPreview;
        UpdatePaletteBoxSize();

        if (mode == PaletteMode.Command)
        {
            _paletteSearch.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, query));
            return;
        }

        if (mode == PaletteMode.Terminal)
        {
            _paletteSearch.Cancel();
            ShowPaletteItems(BuildTerminalMatches(query));
            return;
        }

        if (mode == PaletteMode.All && string.IsNullOrWhiteSpace(query))
        {
            _paletteSearch.Cancel();
            ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, string.Empty));
            return;
        }

        _ = RefilterSearchAsync(mode, query);
    }

    private async Task RefilterSearchAsync(PaletteMode mode, string query)
    {
        var items = await _paletteSearch.SearchLatestAsync(mode, query, _paletteCommands,
            ConnectedCodeLspManagers, FileEntry, GrepEntry, SymbolEntry);
        if (items is not null)
            ShowPaletteItems(items);
    }

    private IReadOnlyList<IEditorLspManager> ConnectedCodeLspManagers()
    {
        var seen = new HashSet<IEditorLspManager>();
        var result = new List<IEditorLspManager>();

        void TryAdd(EditorTab? tab)
        {
            if (tab is null || !_codeSupport.CanHandle(tab.PeekFilePath))
                return;
            var lsp = GetLspManager(tab);
            if (lsp is { IsConnected: true } && seen.Add(lsp))
                result.Add(lsp);
        }

        TryAdd(_activeEditorTab);              // アクティブなコードタブを優先（結果が先頭に来る）
        foreach (var tab in _editorTabs)
            TryAdd(tab);

        return result;
    }

    private PaletteCommand SymbolEntry(LspSymbolInformation sym, string category)
    {
        var path = CodeEditorSupport.TryUriToLocalPath(sym.Location?.Uri);
        var line1 = (sym.Location?.Range?.Start?.Line ?? 0) + 1;
        var title = string.IsNullOrEmpty(sym.ContainerName) ? sym.Name : $"{sym.Name}  ·  {sym.ContainerName}";
        return new PaletteCommand(category, title,
            () => { if (path is not null) _ = OpenAndNavigateAsync(path, line1); })
        {
            PreviewPath = path,
            PreviewLine = line1,
        };
    }

    private IReadOnlyList<PaletteCommand> BuildTerminalMatches(string query)
    {
        if (_activeTerminalTab?.View is not { } view)
            return new[] { TerminalStatus("ターミナルがありません") };

        if (string.IsNullOrWhiteSpace(query))
            return new[] { TerminalStatus("入力してターミナル内を検索") };

        var matches = view.FindMatches(query, caseSensitive: false);
        if (matches.Count == 0)
            return new[] { TerminalStatus("一致なし") };

        const int max = 200; // grep と同様に件数を上限で抑える
        return matches.Take(max).Select(m => TerminalMatchEntry(m, view)).ToList();
    }

    private PaletteCommand TerminalMatchEntry(TerminalMatch match, TerminalTabView view)
        => new($"行 {match.LineIndex + 1}", match.LineText.Trim(), () =>
        {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
            view.SelectMatch(match);
            view.FocusTerminal();
        });

    private static PaletteCommand TerminalStatus(string text)
        => new("ターミナル検索", text, static () => { });

    private void ShowPaletteItems(IReadOnlyList<PaletteCommand> items)
    {
        var (_, query) = CommandPaletteService.Parse(PaletteInput.Text);
        foreach (var item in items)
            item.TitleMatch = query;

        PaletteList.ItemsSource = items;
        if (PaletteList.Items.Count > 0)
        {
            PaletteList.SelectedIndex = 0;
            PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        }
        else
        {
            UpdatePalettePreview(null);
        }
    }

    private PaletteCommand FileEntry(FileSearchHit hit)
        => new("ファイル", hit.RelativePath, () => _ = OpenAndNavigateAsync(hit.FullPath, 0))
        { PreviewPath = hit.FullPath };

    private PaletteCommand GrepEntry(ContentSearchHit hit, string query)
        => new($"{hit.RelativePath}:{hit.Line}", hit.LineText.Trim(),
            () => _ = OpenAndNavigateAsync(hit.FullPath, hit.Line))
        { PreviewPath = hit.FullPath, PreviewLine = hit.Line, PreviewHighlight = query };

    private async Task OpenAndNavigateAsync(string path, int line)
    {
        await OpenFileInNewEditorTabAsync(path);
        if (line > 0 && _activeEditorTab?.Control is { } control)
            control.NavigateTo(line - 1, 0);
    }

    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdatePalettePreview(PaletteList.SelectedItem as PaletteCommand);

    private VimEditorControl? _previewEditor;

    private CancellationTokenSource? _palettePreviewCts;

    private VimEditorControl EnsurePreviewEditor()
    {
        if (_previewEditor is { } existing)
            return existing;

        var editor = new VimEditorControl(new VimEditorControlOptions())
        {
            VimEnabled = false,
            Focusable = false,  // キーボードフォーカスを奪わない（↑↓・Enter はパレットのまま）
        };
        _appearance.ApplyEditorOptions(editor);
        _appearance.ApplyEditorAppearance(editor);
        editor.ExecuteCommand("set number");     // 行番号は常に表示（本体設定に依らず）
        editor.ExecuteCommand("set cursorline"); // ヒット行を常に強調
        editor.ExecuteCommand("set nominimap");  // 狭いプレビューではミニマップは邪魔なので切る
        editor.SetSharedStatusBar(new VimStatusBar());
        PalettePreviewHost.Child = editor;
        _previewEditor = editor;
        return editor;
    }

    private void UpdatePalettePreview(PaletteCommand? command)
    {
        _palettePreviewCts?.Cancel();

        if (command?.PreviewPath is not { } path || !File.Exists(path))
        {
            if (_previewEditor is not null)
                _previewEditor.Visibility = Visibility.Collapsed;
            return;
        }

        var cts = new CancellationTokenSource();
        _palettePreviewCts = cts;
        _ = ShowPalettePreviewAsync(command, path, cts.Token);
    }

    private async Task ShowPalettePreviewAsync(PaletteCommand command, string path, CancellationToken ct)
    {
        try
        {
            await Task.Delay(60, ct); // ↑↓ の連続移動でファイルを開きすぎないよう軽く待つ
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested)
            return;

        var editor = EnsurePreviewEditor();
        editor.Visibility = Visibility.Visible;
        try
        {
            editor.LoadFile(path);
            editor.HighlightSearch(command.PreviewHighlight ?? "");
            NavigatePreview(editor, command);
            _ = editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (!ct.IsCancellationRequested && editor.Visibility == Visibility.Visible)
                    NavigatePreview(editor, command);
            });
        }
        catch
        {
            editor.Visibility = Visibility.Collapsed;
        }
    }

    private static void NavigatePreview(VimEditorControl editor, PaletteCommand command)
    {
        if (command.PreviewLine > 0)
            editor.JumpToLine(command.PreviewLine - 1, 0);
        else
            editor.NavigateTo(0, 0);
    }

    private void ExecutePaletteSelection()
    {
        if (PaletteList.SelectedItem is not PaletteCommand command)
            return;
        CloseCommandPalette(refocus: false);
        command.Execute();
    }

    private void OnPaletteTextChanged(object sender, TextChangedEventArgs e) => RefilterPalette();

    private void OnPaletteInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
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

    private void MovePaletteSelection(int delta)
    {
        var count = PaletteList.Items.Count;
        if (count == 0)
            return;
        PaletteList.SelectedIndex = ((PaletteList.SelectedIndex < 0 ? 0 : PaletteList.SelectedIndex)
            + delta + count) % count;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void OnPaletteBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        => CloseCommandPalette(refocus: true);

    private void OnPaletteBoxMouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteCommand command })
        {
            e.Handled = true;
            CloseCommandPalette(refocus: false);
            command.Execute();
        }
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var list = new List<PaletteCommand>();

        string? Sc(string id) => _keybindings.For(id)?.Format();

        list.Add(new("ステージ",
            _stageActive ? "ステージモードを解除（タイル表示へ）" : "ステージモードへ（舞台＋袖）",
            () => { if (_stageActive) ExitStageMode(); else EnterStageMode(); }));
        if (_stageActive)
            list.Add(new("ステージ", _overviewActive ? "俯瞰を閉じる" : "俯瞰（全カードを一望）",
                ToggleOverview, "Ctrl+W z"));

        foreach (var kind in StageOrder)
        {
            var target = kind;
            list.Add(new("移動", $"{PaneLabel(target)} へ",
                () => { SetPaneVisible(target, true); FocusPane(target); }));
        }

        foreach (var kind in StageOrder)
        {
            var target = kind;
            list.Add(new("ペイン", $"{PaneLabel(target)} の表示を切替",
                () => SetPaneVisible(target, !IsPaneVisible(target))));
        }

        list.Add(new("タブ", "新しいターミナルタブ", () => OnTerminalNewTab(this, new RoutedEventArgs()),
            Sc("tab.newTerminal"), "tab.newTerminal"));
        list.Add(new("タブ", "新しいエディタタブ", () => OnEditorNewTab(this, new RoutedEventArgs()),
            Sc("tab.newEditor"), "tab.newEditor"));
        list.Add(new("タブ", "新しいブラウザタブ", () => OnBrowserNewTab(this, new RoutedEventArgs()),
            Sc("tab.newBrowser"), "tab.newBrowser"));

        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く",
            () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, Sc("composer.run"), "composer.run"));
        list.Add(new("コンポーザ", "本文をペグボードへピン",
            () => OnComposerPinToPegboard(this, new RoutedEventArgs())));

        list.Add(new("ペグボード", "クリップボードから追加",
            () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択をピン", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLをピン", PinBrowserUrlToPegboard));

        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null),
            Sc("sidebar.explorer"), "sidebar.explorer"));
        list.Add(new("サイドバー", "検索（全文検索 / grep）", () => _vm.ShowSearchCommand.Execute(null),
            Sc("sidebar.search"), "sidebar.search"));
        list.Add(new("サイドバー", "タブ一覧", () => _vm.ShowTabsCommand.Execute(null),
            Sc("sidebar.tabs"), "sidebar.tabs"));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null),
            Sc("sidebar.git"), "sidebar.git"));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null),
            Sc("sidebar.pegboard"), "sidebar.pegboard"));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null),
            Sc("sidebar.settings"), "sidebar.settings"));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null),
            Sc("sidebar.appearance"), "sidebar.appearance"));
        list.Add(new("サイドバー", "キーボード設定", () => _vm.ShowKeyboardSettingsCommand.Execute(null)));
        list.Add(new("サイドバー", "エクスプローラで現在のファイルを選択（同期）", RevealActiveFileInFolderTree,
            Sc("explorer.revealActiveFile"), "explorer.revealActiveFile"));

        list.Add(new("AI", "AIセッション一覧を開閉", () => _vm.Sessions.ToggleOpenCommand.Execute(null),
            Sc("sidebar.sessions"), "sidebar.sessions"));

        foreach (var workspace in _vm.Workspaces.Workspaces.Where(w => !w.IsActive))
        {
            var target = workspace;
            list.Add(new("ワークスペース", $"切替: {target.Name}",
                () => _vm.Workspaces.ActivateWorkspaceCommand.Execute(target)));
        }

        return list;
    }
}
