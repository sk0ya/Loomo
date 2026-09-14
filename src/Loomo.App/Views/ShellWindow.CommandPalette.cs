using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: コマンドパレット（部屋全体の操作統一）。移動・ペイン表示・タブ・コンポーザ・ ペグボード・サイドバー・ワークスペース切替といった既存操作に名前を付け、 Ctrl+Shift+P（または Ctrl+W p）から検索して実行できるようにする。 一覧は開くたびに現在状態（ステージ中か・WS一覧など）から組み直す。 絞り込みロジックは <see cref="PaletteFilter"/>（純ロジック・テスト済み）。
/// 先頭1文字（/ # @ :）で「探して飛ぶ」側へ切り替わり（<see cref="PaletteQuery"/>）、選んでいる場所は
/// 開かずに右半分でプレビューする（<see cref="PalettePreviewLoader"/>／<see cref="PalettePreviewView"/>）。
/// 設計書 §24.2。</summary>
public partial class ShellWindow {
    private IReadOnlyList<PaletteCommand> _paletteCommands = Array.Empty<PaletteCommand>();
    private bool IsPaletteOpen => CommandPaletteOverlay.Visibility == Visibility.Visible;
    /// <summary>パレットを開くキー（<c>palette.open</c> / <c>palette.openFromPrefix</c>）の実体。
    /// <b>外からなら開く、中でもう一度押したら検索対象を次へ回す</b>——検索ペインの
    /// <see cref="OpenOrCycleSearch"/>（Ctrl+Shift+F）と同じ流儀で、覚えるキーを1本に保つ。</summary>
    private void OpenOrCyclePalette() {
        if (IsPaletteOpen) {
            CyclePaletteMode(+1);
            return;
        }
        OpenCommandPalette(PaletteMode.Command);
    }
    private void OpenCommandPalette() => OpenCommandPalette(PaletteMode.Command);
    /// <summary>パレットを開く。<paramref name="mode"/> のプレフィックスを入れた状態で開くので、
    /// 「ファイルへ移動」のショートカットから開けばそのまま打ち始められる。
    /// <b>開いている最中に同じ種類のキーを押したら、開き直さずその場で探し方だけ変える</b>
    /// （打った語を捨てないため）。</summary>
    private void OpenCommandPalette(PaletteMode mode) {
        if (IsPaletteOpen) {
            SetPaletteMode(mode);
            return;
        }
        _paletteCommands = BuildPaletteCommands();
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        PaletteHint.Text = PaletteQuery.HintWith(
            _keybindings.For("palette.nextScope")?.Format(), _keybindings.For("palette.open")?.Format());
        UpdatePaletteBoxSize();
        PaletteInput.Text = PaletteQuery.PrefixOf(mode);
        PaletteInput.CaretIndex = PaletteInput.Text.Length;
        RefilterPalette();
        PaletteInput.Focus();
    }
    /// <summary>検索対象（探し方）を1つ進める／戻す。キーバインドから呼ばれる
    /// （<c>palette.nextScope</c> ／ <c>palette.previousScope</c>）。</summary>
    private void CyclePaletteMode(int direction) {
        if (!IsPaletteOpen)
            return;
        SetPaletteMode(PaletteQuery.NextMode(PaletteQuery.Parse(PaletteInput.Text).Mode, direction));
    }
    /// <summary>打った語はそのままに、探し方（先頭のプレフィックス）だけ差し替える。
    /// 入力欄の TextChanged 経由で一覧・プレビューも組み直る。</summary>
    private void SetPaletteMode(PaletteMode mode) {
        if (!IsPaletteOpen)
            return;
        PaletteInput.Text = PaletteQuery.Parse(PaletteInput.Text).ToInput(mode);
        PaletteInput.CaretIndex = PaletteInput.Text.Length;
        PaletteInput.Focus();
    }
    // 大きさの基準はウィンドウではなくオーバーレイ自身（＝実際に被せている領域）。ウィンドウ幅で
    // 計算すると、袖やドックで狭まった領域から箱がはみ出し、左右が切れて項目名が読めなくなる。
    private void UpdatePaletteBoxSize() {
        _paletteView.UpdateSize(CommandPaletteOverlay.ActualWidth, CommandPaletteOverlay.ActualHeight);
    }
    private void OnPaletteOverlaySizeChanged(object sender, SizeChangedEventArgs e) {
        if (IsPaletteOpen)
            UpdatePaletteBoxSize();
    }
    private void CloseCommandPalette(bool refocus) {
        if (!IsPaletteOpen)
            return;
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        _paletteSearch.Cancel();
        _paletteView.SetPreviewVisible(false);
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }
    // ここから下がパレットの「探して飛ぶ」側（§24.2）。先頭1文字でモードを決め、コマンドのときだけ
    // 手元の一覧を絞り、それ以外は検索サービス／言語サーバーへ投げて結果を一覧に出す。
    private void RefilterPalette() {
        var query = PaletteQuery.Parse(PaletteInput.Text);
        _paletteSearch.CancelSearch();
        _paletteView.SetPreviewVisible(query.IsNavigation);
        switch (query.Mode) {
            case PaletteMode.Command:
                PaletteStatus.Text = "";
                ShowPaletteItems(PaletteFilter.Filter(_paletteCommands, query.Text), query.Text);
                break;
            case PaletteMode.Line:
                ShowPaletteLineItem(query);
                break;
            default:
                _ = RunPaletteSearchAsync(query);
                break;
        }
    }
    private void ShowPaletteItems(IReadOnlyList<PaletteCommand> items, string query) {
        // 候補が無いときはプレビュー欄ごと畳む（空の枠だけ残ると、読み込み中との区別がつかない）。
        // 空にすると選択が変わらない＝SelectionChanged が来ないので、残像もここで消す。
        if (items.Count == 0) {
            ClearPalettePreview();
            _paletteView.SetPreviewVisible(false);
        }
        _paletteView.ShowItems(items, query);
    }
    /// <summary>探して飛ぶ側の一覧。待ち・キャンセル・供給元の振り分けは
    /// <see cref="PaletteSearchCoordinator"/> の仕事で、ここは返ってきたものを描くだけ。</summary>
    private async Task RunPaletteSearchAsync(PaletteQuery query) {
        var outcome = await _paletteSearch.SearchAsync(query, JumpToPaletteTarget, s => PaletteStatus.Text = s);
        if (outcome is null || !IsPaletteOpen)
            return;
        ShowPaletteItems(outcome.Items, query.Text);
        PaletteStatus.Text = outcome.Status;
    }
    /// <summary>「: 行番号」モード。対象はいま見ているファイルなので、検索は要らずその場で1件作る。</summary>
    private void ShowPaletteLineItem(PaletteQuery query) {
        if (ActiveEditorFilePath() is not { } path) {
            ShowPaletteItems(Array.Empty<PaletteCommand>(), "");
            PaletteStatus.Text = "開いているファイルがありません";
            return;
        }
        if (query.LineNumber is not { } line) {
            ShowPaletteItems(Array.Empty<PaletteCommand>(), "");
            PaletteStatus.Text = "行番号を入力してください";
            return;
        }
        ShowPaletteItems(
            PaletteNavigationItems.ForLine(path, _workspace.ToDisplayPath(path), line, JumpToPaletteTarget), "");
        PaletteStatus.Text = "";
    }
    private string? ActiveEditorFilePath() {
        var path = _activeEditorTab is { } tab ? (tab.IsRealized ? tab.Control.FilePath : tab.PeekFilePath) : null;
        return string.IsNullOrEmpty(path) ? null : path;
    }
    /// <summary>確定（Enter／クリック）で実際に飛ぶ。プレビューで見ていた場所と同じ <see cref="PaletteTarget"/>
    /// を使うので、見えていた行と開いた行がズレない。</summary>
    private Action JumpToPaletteTarget(PaletteTarget target) => () => _ = OpenPaletteTargetAsync(target);
    private async Task OpenPaletteTargetAsync(PaletteTarget target) {
        await OpenFileInNewEditorTabAsync(target.FullPath);
        if (_activeEditorTab is { IsRealized: true } tab) {
            if (target.Line > 0)
                tab.Control.NavigateTo(target.Line - 1, Math.Max(0, target.Column - 1));
            if (!string.IsNullOrEmpty(target.Highlight))
                tab.Control.HighlightSearch(target.Highlight);
        }
        FocusPane(sk0ya.Loomo.Core.Files.BinaryFileDetector.IsBinary(target.FullPath) ? PaneKind.EditorSupport : PaneKind.Editor);
    }
    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPalettePreview();
    /// <summary>選択が動くたびに、いま選んでいる場所の中身を出し直す。</summary>
    private void ShowPalettePreview() {
        if (!IsPaletteOpen || !_paletteView.IsPreviewVisible)
            return;
        if (PaletteList.SelectedItem is not PaletteCommand { Target: { } target }) {
            ClearPalettePreview();
            return;
        }
        _ = LoadPalettePreviewAsync(target);
    }
    private async Task LoadPalettePreviewAsync(PaletteTarget target) {
        var content = await _paletteSearch.PreviewAsync(target, _workspace.ToDisplayPath(target.FullPath));
        if (content is null || !IsPaletteOpen || !_paletteView.IsPreviewVisible)
            return;
        _paletteView.ShowPreview(content);
    }
    private void ClearPalettePreview() {
        _paletteSearch.CancelPreview();
        if (_paletteView.IsPreviewVisible)
            _paletteView.ShowPreview(PalettePreviewContent.Empty);
    }
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
            case Key.PageDown or Key.PageUp:
                MovePaletteSelection(e.Key == Key.PageDown ? 10 : -10);
                e.Handled = true;
                break;
        }
    }
    /// <summary>パレットを開いている間に通すコマンド（＝この面自身の操作）。
    /// 「探し方の切替」と「別の探し方で開き直す」はここに入り、それ以外は入力欄へ素通しする。</summary>
    private static bool IsPaletteScopedCommand(string id)
        => id.StartsWith("palette.", StringComparison.Ordinal);
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
        // 3モードは「行き先」を出す（トグル1本だと、いまどこに居るかを覚えていないと押せない）。
        if (!_stageActive)
            list.Add(new("並べ方", "集中にする（1つを大きく表示）", EnterStageMode));
        if (_stageActive || _dockActive)
            list.Add(new("並べ方", "分割にする（複数の画面を並べる）", () => { ExitStageMode(); ExitDockMode(); }));
        if (!_dockActive)
            list.Add(new("並べ方", "ドックにする（道具を下と右の領域へ）", EnterDockMode));
        if (_stageActive)
            list.Add(new("並べ方", _overviewActive ? "すべての画面の一覧を閉じる" : "すべての画面を一覧表示", ToggleOverview, "Ctrl+W z"));
        // 「部屋に出す／しまう」はタイルの話。ドックでは同じ操作が「その領域の1枚にする／畳む」に
        // なるので、ここで読み替える——タイルの木をそのまま触ると、見えているのは中央なのに
        // 畳まれるのは裏の木で、押しても何も起きない操作になる（中央も選べなくなる）。
        foreach (var kind in PaneOrderForMode()) {
            var target = kind;
            list.Add(new("移動", $"{PaneLabel(target)} へ", () => {
                if (!_dockActive)
                    SetPaneVisible(target, true);
                FocusPane(target);   // ドックは FocusPane が畳んである面をその領域へ出す
            }));
        }
        foreach (var kind in PaneOrderForMode()) {
            var target = kind;
            list.Add(new("ペイン", $"{PaneLabel(target)} の表示を切替", () => {
                if (_dockActive)
                    ToggleDockPane(target);
                else
                    SetPaneVisible(target, !IsPaneVisible(target));
            }));
        }
        // パレットの中で探し方を切り替える口（プレフィックスを知らなくても辿り着けるように）。
        list.Add(new("移動", "ファイルを検索して開く", () => OpenCommandPalette(PaletteMode.File),
            Sc("palette.goToFile"), "palette.goToFile"));
        list.Add(new("移動", "テキストを検索して開く", () => OpenCommandPalette(PaletteMode.Text),
            Sc("palette.goToText"), "palette.goToText"));
        list.Add(new("移動", "シンボルを検索して開く", () => OpenCommandPalette(PaletteMode.Symbol),
            Sc("palette.goToSymbol"), "palette.goToSymbol"));
        list.Add(new("移動", "現在のファイルの行へ移動", () => OpenCommandPalette(PaletteMode.Line),
            Sc("palette.goToLine"), "palette.goToLine"));
        list.Add(new("検索", "検索を開く／検索対象を次へ切り替え", OpenOrCycleSearch,
            Sc("pane.search"), "pane.search"));
        list.Add(new("検索", "次の検索対象へ切り替え", () => CycleSearchScope(+1),
            Sc("search.nextScope"), "search.nextScope"));
        list.Add(new("検索", "前の検索対象へ切り替え", () => CycleSearchScope(-1),
            Sc("search.previousScope"), "search.previousScope"));
        AddSearchScopeCommand(list, "テキスト", SearchScope.Text);
        AddSearchScopeCommand(list, "ファイル", SearchScope.FileName);
        AddSearchScopeCommand(list, "ターミナル", SearchScope.Terminal);
        AddSearchScopeCommand(list, "クラス", SearchScope.Class);
        AddSearchScopeCommand(list, "シンボル", SearchScope.Symbol);
        list.Add(new("タブ", "新しいターミナルタブ", () => OnTerminalNewTab(this, new RoutedEventArgs()), Sc("tab.newTerminal"), "tab.newTerminal"));
        list.Add(new("タブ", "新しいエディタタブ", () => OnEditorNewTab(this, new RoutedEventArgs()), Sc("tab.newEditor"), "tab.newEditor"));
        list.Add(new("タブ", "新しいブラウザタブ", () => OnBrowserNewTab(this, new RoutedEventArgs()), Sc("tab.newBrowser"), "tab.newBrowser"));
        // 意味的な選択（§24.9）。キー・右クリックメニューと同じ実装へ入る（§31.2 原則6）。
        list.Add(new("エディタ", "現在のファイルを保存", SaveActiveEditor, Sc("editor.save"), "editor.save"));
        list.Add(new("エディタ", "選択を意味的に広げる", ExpandSemanticSelection, Sc("editor.selection.expand"), "editor.selection.expand"));
        list.Add(new("エディタ", "選択を1段戻す", ShrinkSemanticSelection, Sc("editor.selection.shrink"), "editor.selection.shrink"));
        if (ActiveCSharpEditor() is not null)
        {
            foreach (var descriptor in CSharpEditorCommandCatalog.All)
            {
                var id = descriptor.Id;
                list.Add(new("C# エディタ", descriptor.Title,
                    () => ExecuteCSharpEditorCommand(id), Sc(id), id));
            }
        }
        // ガターの ▶ はマウス専用なので、キーボードからの実行経路はここが受け持つ（§24）。
        // 対象が無いときは項目自体を出さない——押せるのに何も起きない項目を作らないため。
        if (ActiveEditorTestAtCaret() is { } caretTest)
            list.Add(new("エディタ", $"カーソル行のテストを実行: {caretTest.Test.DisplayName}",
                RunTestAtCaret, Sc("editor.test.runAtCaret"), "editor.test.runAtCaret"));
        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く", () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, Sc("composer.run"), "composer.run"));
        list.Add(new("コンポーザ", "本文をペグボードへ残す", () => OnComposerPinToPegboard(this, new RoutedEventArgs())));
        list.Add(new("ペグボード", "クリップボードを残す", () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択を残す", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLを残す", PinBrowserUrlToPegboard));
        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null), Sc("sidebar.explorer"), "sidebar.explorer"));
        list.Add(new("サイドバー", "タブセクションを開閉", () => _vm.ShowTabsCommand.Execute(null), Sc("sidebar.tabs"), "sidebar.tabs"));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null), Sc("sidebar.git"), "sidebar.git"));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null), Sc("sidebar.pegboard"), "sidebar.pegboard"));
        if (_vm.IsCSharpSolutionAvailable)
            list.Add(new("サイドバー", "ソリューション（C#）", () => _vm.ShowSolutionCommand.Execute(null), Sc("sidebar.solution"), "sidebar.solution"));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null), Sc("sidebar.settings"), "sidebar.settings"));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null), Sc("sidebar.appearance"), "sidebar.appearance"));
        list.Add(new("サイドバー", "キーボード設定", () => _vm.ShowKeyboardSettingsCommand.Execute(null)));
        list.Add(new("サイドバー", "エクスプローラで現在のファイルを選択（同期）", RevealActiveFileInFolderTree, Sc("explorer.revealActiveFile"), "explorer.revealActiveFile"));
        list.Add(new("AI", "AIセッション一覧を開閉", () => _vm.Sessions.ToggleOpenCommand.Execute(null), Sc("sidebar.sessions"), "sidebar.sessions"));
        foreach (var workspace in _vm.Workspaces.Workspaces.Where(w => !w.IsActive)) {
            var target = workspace;
            list.Add(new("ワークスペース", $"切替: {target.Name}", () => _ = _vm.Workspaces.ActivateWorkspaceAsync(target)));
        }
        return list;
    }

    private void AddSearchScopeCommand(List<PaletteCommand> list, string label, SearchScope scope)
    {
        var id = $"search.scope.{scope switch
        {
            SearchScope.Text => "text",
            SearchScope.FileName => "fileName",
            SearchScope.Terminal => "terminal",
            SearchScope.Class => "class",
            SearchScope.Symbol => "symbol",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        }}";
        list.Add(new("検索", $"検索対象を{label}にする", () => OpenSearch(scope),
            _keybindings.For(id)?.Format(), id));
    }
}
