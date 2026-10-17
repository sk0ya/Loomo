using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: コマンドパレット（部屋全体の操作統一）。移動・ペイン表示・タブ・コンポーザ・ ペグボード・サイドバー・ワークスペース切替といった既存操作に名前を付け、 Ctrl+Shift+P（または Ctrl+W p）から検索して実行できるようにする。 一覧は開くたびに現在状態（ステージ中か・WS一覧など）から組み直す。 絞り込みロジックは <see cref="PaletteFilter"/>（純ロジック・テスト済み）。
/// 先頭1文字（/ # @ :）で「探して飛ぶ」側へ切り替わり（<see cref="PaletteQuery"/>）、選んでいる場所は
/// 開かずに右半分でプレビューする（<see cref="PalettePreviewLoader"/>／<see cref="PalettePreviewView"/>）。
/// 右半分はコマンドのときも空けず、選んでいるコマンドの詳細を出す——探し方を切り替えても箱と列の
/// 大きさが変わらないので、目で追っている行が横へ流れない。
/// 設計書 §24.2。</summary>
public partial class ShellWindow {
    private IReadOnlyList<PaletteCommand> _paletteCommands = Array.Empty<PaletteCommand>();
    /// <summary>項目を選ばずに閉じられた（Escape・外側クリック・一覧の差し替え）ときに1回だけ呼ぶ。
    /// パレットを「選んでもらう問い」として使う側（タスクの pickString 入力）が、取り消しを知るため。</summary>
    private Action? _paletteDismissed;
    private CommandPaletteSearchPresenter? _paletteSearchPresenter;
    private CommandPaletteSearchPresenter PaletteSearchPresentation
        => _paletteSearchPresenter ??= new CommandPaletteSearchPresenter(
            _paletteSearch, _paletteView, PaletteStatus, () => IsPaletteOpen, JumpToPaletteTarget);
    private bool IsPaletteOpen => CommandPaletteOverlay.Visibility == Visibility.Visible;
    /// <summary>パレットを開くキー（<c>palette.open</c> / <c>palette.openFromPrefix</c>）の実体。
    /// <b>外からなら開く、中でもう一度押したら検索対象を次へ回す</b>——検索ペインの
    /// <see cref="OpenOrCycleSearch"/>（Ctrl+Shift+F）と同じ流儀で、覚えるキーを1本に保つ。</summary>
    private void OpenOrCyclePalette() {
        if (IsPaletteOpen) {
            CyclePaletteMode(+1);
            return;
        }
        OpenCommandPalette(PaletteMode.All);
    }
    private void OpenCommandPalette() => OpenCommandPalette(PaletteMode.All);
    /// <summary>パレットを開く。<paramref name="mode"/> のプレフィックスを入れた状態で開くので、
    /// 「ファイルへ移動」のショートカットから開けばそのまま打ち始められる。
    /// <b>開いている最中に同じ種類のキーを押したら、開き直さずその場で探し方だけ変える</b>
    /// （打った語を捨てないため）。</summary>
    /// <para><paramref name="commands"/> を渡すと一覧をそれだけに絞って開く（「最近のコマンドを選び直す」など、
    /// 部屋の操作全体ではなく特定の候補から選ばせたいとき）。開いている最中なら一覧だけ差し替える。</para>
    private void OpenCommandPalette(PaletteMode mode, IReadOnlyList<PaletteCommand>? commands = null) {
        if (IsPaletteOpen) {
            if (commands is not null) {
                TakePaletteDismissed()?.Invoke();
                _paletteCommands = commands;
                PaletteInput.Text = PaletteQuery.PrefixOf(mode);
                PaletteInput.CaretIndex = PaletteInput.Text.Length;
                RefilterPalette();
                PaletteInput.Focus();
                return;
            }
            SetPaletteMode(mode);
            return;
        }
        _paletteCommands = commands ?? BuildPaletteCommands();
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        SetPaletteHint(mode);
        UpdatePaletteBoxSize();
        PaletteInput.Text = PaletteQuery.PrefixOf(mode);
        PaletteInput.CaretIndex = PaletteInput.Text.Length;
        RefilterPalette();
        PaletteInput.Focus();
    }
    /// <summary>探し方の案内をそのままクリックできるようにし、選択中のモードをアクセント色で示す。</summary>
    private void SetPaletteHint(PaletteMode activeMode) {
        PaletteHint.Inlines.Clear();
        for (var i = 0; i < PaletteHintPolicy.Modes.Count; i++) {
            if (i > 0)
                PaletteHint.Inlines.Add(new Run("    "));

            var (mode, label) = PaletteHintPolicy.Modes[i];
            var hint = new Hyperlink(new Run(label)) {
                Tag = mode,
                Cursor = Cursors.Hand,
                TextDecorations = null,
                FontWeight = mode == activeMode ? FontWeights.SemiBold : FontWeights.Normal,
            };
            hint.SetResourceReference(TextElement.ForegroundProperty, mode == activeMode ? "Accent" : "FgDim");
            hint.MouseEnter += (_, _) => hint.TextDecorations = TextDecorations.Underline;
            hint.MouseLeave += (_, _) => hint.TextDecorations = null;
            hint.Click += OnPaletteHintModeClick;
            PaletteHint.Inlines.Add(hint);
        }

        var switchHint = PaletteQuery.HintWith(
            _keybindings.For("palette.nextScope")?.Format(), _keybindings.For("palette.open")?.Format());
        if (switchHint != PaletteQuery.ModesHint)
            PaletteHint.Inlines.Add(new Run(switchHint[PaletteQuery.ModesHint.Length..]));
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
    private void OnPaletteHintModeClick(object sender, RoutedEventArgs e) {
        if (sender is Hyperlink { Tag: PaletteMode mode })
            SetPaletteMode(mode);
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
        TakePaletteDismissed()?.Invoke();
        _paletteSearch.Cancel();
        _paletteView.SetNavigation(false);
        if (refocus && _focusedRegion?.Pane is { } pane)
            FocusPane(pane);
    }
    // ここから下がパレットの「探して飛ぶ」側（§24.2）。先頭1文字でモードを決め、コマンドのときだけ
    // 手元の一覧を絞り、それ以外は検索サービス／言語サーバーへ投げて結果を一覧に出す。
    private void RefilterPalette()
        => _ = PaletteSearchPresentation.RefilterAsync(
            PaletteInput.Text, _paletteCommands, SetPaletteHint,
            ActiveEditorFilePath(), _workspace.ToDisplayPath, BuildRecentTerminalPaletteCommands);
    private string? ActiveEditorFilePath()
        => EditorTabNavigationPolicy.ActiveFilePath(_activeEditorTab);
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
    /// <summary>選択が動くたびに、いま選んでいるものの中身を右の欄へ出し直す。場所（探して飛ぶ側）は
    /// ファイルの中身、コマンドはその詳細——どちらの探し方でも右の欄は空けない（§24.2）。</summary>
    private void ShowPalettePreview() {
        if (!IsPaletteOpen)
            return;
        if (PaletteList.SelectedItem is not PaletteCommand command) {
            ClearPalettePreview();
            return;
        }
        if (command.Target is { } target)
            _ = LoadPalettePreviewAsync(target);
        else
            ShowPaletteCommandDetail(command);
    }
    /// <summary>コマンドを選んでいるときの右の欄。押す前に分かるのは「何の仲間か・キーがあるか」なので、
    /// それだけを出す（読み込みは要らないので同期）。</summary>
    private void ShowPaletteCommandDetail(PaletteCommand command) {
        _paletteSearch.CancelPreview();
        _paletteView.ShowPreviewDetail(PaletteCommandPreview.Create(command));
    }
    private async Task LoadPalettePreviewAsync(PaletteTarget target) {
        var content = await _paletteSearch.PreviewAsync(target, _workspace.ToDisplayPath(target.FullPath));
        // 「探して飛ぶ」側でなくても、場所を持つ行（最近の場所・ファイル構造など）は中身を見せる。
        // 待っている間に選択が動いていたら捨てる（追い越された読み込みで右の欄を戻さない）。
        if (content is null || !IsPaletteOpen || PaletteList.SelectedItem is not PaletteCommand { Target: { } selected }
            || selected != target)
            return;
        _paletteView.ShowPreview(content);
    }
    private void ClearPalettePreview() {
        _paletteSearch.CancelPreview();
        _paletteView.ClearPreview();
    }
    private Action? TakePaletteDismissed() {
        var dismissed = _paletteDismissed;
        _paletteDismissed = null;
        return dismissed;
    }
    private void ExecutePaletteSelection() {
        if (PaletteList.SelectedItem is not PaletteCommand command)
            return;
        _paletteDismissed = null;
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
            _paletteDismissed = null;
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
        list.Add(new("ナビゲーション", "前にいた場所へ戻る", () => { _ = NavigateEditorLocationAsync(back: true); },
            Sc("nav.back"), "nav.back"));
        list.Add(new("ナビゲーション", "戻る前の場所へ進む", () => { _ = NavigateEditorLocationAsync(back: false); },
            Sc("nav.forward"), "nav.forward"));
        list.Add(new("ナビゲーション", "最後に編集した場所へ", GoToLastEditLocation,
            Sc("nav.lastEditLocation"), "nav.lastEditLocation"));
        list.Add(new("ナビゲーション", "最近のファイル", OpenRecentFilesOrToggle, Sc("nav.recentFiles"), "nav.recentFiles"));
        list.Add(new("ナビゲーション", "最近の場所", () => { _ = OpenRecentLocationsAsync(); },
            Sc("nav.recentLocations"), "nav.recentLocations"));
        list.Add(new("ナビゲーション", "テスト⇔実装を行き来する", () => { _ = GoToRelatedTestAsync(); },
            Sc("nav.relatedTest"), "nav.relatedTest"));
        list.Add(new("ナビゲーション", "ファイル構造（宣言の一覧）", () => { _ = OpenFileStructureAsync(); },
            Sc("nav.fileStructure"), "nav.fileStructure"));
        list.Add(new("問題", "表示中の問題を抜粋タブで開く",
            () => CurrentProblems().OpenDisplayedAsExcerptsCommand.Execute(null),
            Sc("problems.openAsExcerpts"), "problems.openAsExcerpts"));
        list.Add(new("ターミナル", "最近のコマンドを選び直す", OpenRecentTerminalCommands,
            Sc("terminal.recentCommands"), "terminal.recentCommands"));
        list.Add(new("ターミナル", "コマンドの出力を前回と比較", OpenTerminalOutputComparisons,
            Sc("terminal.compareOutput"), "terminal.compareOutput"));
        list.Add(new("ターミナル", "コマンドの出力をエディタで開く", OpenTerminalOutputs,
            Sc("terminal.openOutput"), "terminal.openOutput"));
        list.Add(new("タスク", "タスクを実行…", OpenWorkspaceTasks, Sc("tasks.run"), "tasks.run"));
        // ローカル履歴と NuGet（§36）。
        list.Add(new("ローカル履歴", "ローカル履歴を表示（版 ↔ いまの差分）", ShowLocalHistory, Sc("localHistory.show"), "localHistory.show"));
        list.Add(new("ローカル履歴", "ローカル履歴から戻す…", RestoreFromLocalHistory, Sc("localHistory.restore"), "localHistory.restore"));
        list.Add(new("ローカル履歴", "最近の変更（全ファイル）", ShowRecentLocalHistory, Sc("localHistory.recent"), "localHistory.recent"));
        list.Add(new("ローカル履歴", "削除したファイルを復元", RestoreDeletedFileFromLocalHistory, Sc("localHistory.restoreDeleted"), "localHistory.restoreDeleted"));
        list.Add(new("NuGet", "NuGet パッケージを管理…", () => OpenNuGetPackageManager(), Sc("nuget.manage"), "nuget.manage"));
        list.Add(new("Claude Code", "Claude Code のセッションを再開…", OpenClaudeSessions, Sc("claude.resumeSession"), "claude.resumeSession"));
        list.Add(new("Claude Code", "Claude Code のフックを設定（承認待ち・セッションを Loomo へ知らせる）", InstallClaudeHooks,
            Sc("claude.installHooks"), "claude.installHooks"));
        list.Add(new("Claude Code", "Claude Code のフックを外す", UninstallClaudeHooks, Sc("claude.uninstallHooks"), "claude.uninstallHooks"));
        list.Add(new("文字コード", "文字コードを調べる（BOM・改行・見えない文字・文字化け）", InspectActiveFileEncoding,
            Sc("editor.inspectEncoding"), "editor.inspectEncoding"));
        list.Add(new("タスク", "既定のビルドタスクを実行", RunDefaultBuildTask,
            Sc("tasks.runDefaultBuild"), "tasks.runDefaultBuild"));
        if (_taskRunner?.ActiveWatch is { } watch)
            list.Add(new("タスク", $"watch タスク「{watch.Label}」の追跡をやめる", StopWatchingTask));
        list.Add(new("ターミナル", "コマンドの出力をペグボードへ残す", OpenTerminalOutputsToPin,
            Sc("terminal.pinOutput"), "terminal.pinOutput"));
        list.Add(new("ターミナル", "ターミナルの選択をペグボードへ残す", PinActiveTerminalSelection,
            Sc("terminal.pinSelection"), "terminal.pinSelection"));
        list.Add(new("軌跡", "いまの地点にしおりを付ける", () => BookmarkTrailEntry(_vm.Trail.CurrentEntry),
            Sc("trail.bookmark"), "trail.bookmark"));
        list.Add(new("軌跡", "しおり一覧", OpenTrailBookmarks, Sc("trail.bookmarks"), "trail.bookmarks"));
        list.Add(new("軌跡", "この日のまとめ", ShowTrailDaySummary, Sc("trail.daySummary"), "trail.daySummary"));
        list.Add(new("軌跡", "留守中のまとめをもう一度表示", ReshowAwaySummary, Sc("trail.reshowAway"), "trail.reshowAway"));
        list.Add(new("軌跡", "軌跡の展開ビューを開く／閉じる", ToggleTrailExpanded, Sc("trail.toggleExpanded"), "trail.toggleExpanded"));
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
        else if (_activeEditorTab is { IsRealized: true } neutralTab && neutralTab.Control.FilePath is { Length: > 0 })
        {
            // C# 以外でも、定義・参照・名前変更などの言語サーバー操作はパレットから引ける（§36）。
            foreach (var descriptor in CSharpEditorCommandCatalog.All)
            {
                if (!CSharpEditorCommandDispatchPolicy.IsLanguageNeutral(descriptor.Id))
                    continue;
                var id = descriptor.Id;
                list.Add(new("エディタ", descriptor.Title, () => ExecuteCSharpEditorCommand(id), Sc(id), id));
            }
        }
        // ガターの ▶ はマウス専用なので、キーボードからの実行経路はここが受け持つ（§24）。
        // 対象が無いときは項目自体を出さない——押せるのに何も起きない項目を作らないため。
        if (ActiveEditorTestAtCaret() is { } caretTest)
            list.Add(new("エディタ", $"カーソル行のテストを実行: {caretTest.Test.DisplayName}",
                RunTestAtCaret, Sc("editor.test.runAtCaret"), "editor.test.runAtCaret"));
        if (ActiveEditorTestAtCaret() is { } caretDebugTest)
            list.Add(new("エディタ", $"カーソル行のテストをデバッグ: {caretDebugTest.Test.DisplayName}",
                DebugTestAtCaret, Sc("editor.test.debugAtCaret"), "editor.test.debugAtCaret"));
        // 失敗の再実行もテストビューのボタンだけにしない。失敗が残っているランナーの分だけ出す。
        foreach (var (label, command) in new (string, ICommand)[]
                 {
                     (".NET", _vm.Debug.Tests.RerunFailedCommand),
                     ("TypeScript", _vm.TsIde.Tests.RerunFailedCommand),
                     ("pytest", _vm.Debug.PyTests.RerunFailedCommand),
                 })
        {
            if (command.CanExecute(null))
                list.Add(new("テスト", $"失敗したテストを再実行（{label}）", () => command.Execute(null)));
        }
        list.Add(new("コンポーザ", IsComposerVisible ? "コンポーザを閉じる" : "コンポーザを開く", () => SetComposerVisible(!IsComposerVisible)));
        list.Add(new("コンポーザ", "本文をターミナルで実行", RunComposer, Sc("composer.run"), "composer.run"));
        list.Add(new("コンポーザ", "本文をペグボードへ残す", () => OnComposerPinToPegboard(this, new RoutedEventArgs())));
        list.Add(new("ペグボード", "クリップボードを残す", () => _vm.Pegboard.AddFromClipboardCommand.Execute(null)));
        list.Add(new("ペグボード", "エディタの選択を残す", PinEditorSelectionToPegboard));
        list.Add(new("ペグボード", "ブラウザのURLを残す", PinBrowserUrlToPegboard));
        list.Add(new("サイドバー", "エクスプローラ", () => _vm.ShowExplorerCommand.Execute(null), Sc("sidebar.explorer"), "sidebar.explorer"));
        list.Add(new("サイドバー", "タブ一覧を開閉", () => _vm.ShowTabsCommand.Execute(null), Sc("sidebar.tabs"), "sidebar.tabs"));
        list.Add(new("サイドバー", "Git", () => _vm.ShowGitCommand.Execute(null), Sc("sidebar.git"), "sidebar.git"));
        list.Add(new("サイドバー", "ペグボード", () => _vm.ShowPegboardCommand.Execute(null), Sc("sidebar.pegboard"), "sidebar.pegboard"));
        if (_vm.IsCSharpSolutionAvailable)
            list.Add(new("サイドバー", "ソリューション（C#）", () => _vm.ShowSolutionCommand.Execute(null), Sc("sidebar.solution"), "sidebar.solution"));
        list.Add(new("サイドバー", "設定", () => _vm.ShowSettingsCommand.Execute(null), Sc("sidebar.settings"), "sidebar.settings"));
        list.Add(new("サイドバー", "外観（テーマ）", () => _vm.ShowAppearanceCommand.Execute(null), Sc("sidebar.appearance"), "sidebar.appearance"));
        list.Add(new("サイドバー", "キーボードショートカット", () => _vm.ShowKeyboardSettingsCommand.Execute(null)));
        list.Add(new("サイドバー", "エクスプローラで現在のファイルを選択（同期）", RevealActiveFileInFolderTree, Sc("explorer.revealActiveFile"), "explorer.revealActiveFile"));
        list.Add(new("AI", "AIセッション一覧を開閉", () => _vm.Sessions.ToggleOpenCommand.Execute(null), Sc("sidebar.sessions"), "sidebar.sessions"));
        foreach (var workspace in _vm.Workspaces.Workspaces.Where(w => !w.IsActive)) {
            var target = workspace;
            list.Add(new("ワークスペース", $"切替: {target.Name}", () => _ = _vm.Workspaces.ActivateWorkspaceAsync(target)));
        }
        // 最近のコマンド（§24.20）はここに混ぜない。「$」の探し方（PaletteMode.Terminal）で別に引く。
        return list;
    }

    private void AddSearchScopeCommand(List<PaletteCommand> list, string label, SearchScope scope)
    {
        var id = PaletteSearchScopePolicy.KeybindingId(scope);
        list.Add(new("検索", $"検索対象を{label}にする", () => OpenSearch(scope),
            _keybindings.For(id)?.Format(), id));
    }
}
