using sk0ya.Loomo.Core.Markdown;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナル／エディタの選択テキストに対する右クリックアクション （「AIへ送る」＝AIバーへ即送信、「ブラウザへ送る」＝内蔵ブラウザでBing検索）。 素材を別のペインへ渡す操作はすべて「〜へ送る」で揃える（設計書 §23.3 の共通語彙）。 メニュー項目はライブラリ側の ContextMenuBuilding フックで各コントロールのネイティブメニュー末尾へ 追加する（選択があるときだけ。スタイルはライブラリが自前のメニュー様式に合わせる）。
/// <para><b>並びは4つの束</b>——①別のペインへ送る ②コードを操作する ③このファイルを扱う ④版と実行——で、
/// 束ごとに区切り線を<b>1本だけ</b>置く（<see cref="AddMenuGroup"/>）。以前は寄稿する
/// <c>Add*</c> がそれぞれ区切り線を足していたため、.cs を右クリックすると区切り線7本・
/// トップレベル19項目がネイティブ項目20項目の下に続き、1画面に収まらなかった。
/// 3項目以上になる系統（Git・デバッグ・C#）はサブメニューへ畳む。</para></summary>
public partial class ShellWindow {
    private void OnEditorContextMenuBuilding(object? sender, EditorContextMenuBuildingEventArgs e) {
        var control = sender as VimEditorControl ?? _activeEditorTab?.Control;
        if (e.BlameLine is { } blame && control is not null) {
            AddBlameCommitMenuItems(e.Menu, control, blame);
            return;
        }
        // ⓪ ネイティブ項目の取捨。右クリック位置（＝キャレット位置）は、説明ポップアップを
        //    そこへ出すためにこの時点で読む——項目を押す頃にはマウスはメニューの上にいる。
        AdjustNativeEditorMenuItems(
            e.Menu, control, control is not null ? Mouse.GetPosition(control) : new Point());
        // ① 素材を別のペインへ送る（§23.3 の「〜へ送る」）
        AddMenuGroup(e.Menu, menu => SelectionActionMenuBuilder.AddSelectionMenuItems(
            menu, e.SelectedText, e.HasSelection,
            SelectionActionMenuBuilder.BuildEditorSendMenuItem(
                _workspace, e.SelectedText, e.HasSelection, workingDirectory: null,
                currentDocumentPath: control?.FilePath,
                openLocation: location => _ = SendLocationToEditorAsync(location)),
            SelectionActionMenuBuilder.BuildDiffSendMenu(CompareEntries(
                control, SelectionActionPresentation.SelectionSourceLabel(control?.FilePath),
                e.SelectedText, e.HasSelection)),
            !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
            () => {
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
                _vm.AiBar.AskAbout(e.SelectedText);
            },
            text => _ = SearchSelectionInBrowserAsync(text),
            AddWorkflowMenuItems));
        // 「どこかへ行く」操作はコントロール側の「移動」サブメニューへ入れる。
        // Loomo の定義 Peek だけ別の場所に出ていると、移動の入口が2つに割れる。
        AddCSharpPeekMenuItem(e.NavigateMenu, control);
        // ② コードを選ぶ・書き換える
        AddMenuGroup(e.Menu, menu => {
            AddSemanticSelectionMenuItems(menu, control);
            AddRefactorMenuItems(menu, control);
            AddCSharpMenuItems(menu, control);
        });
        // ③ このファイルそのものを扱う
        AddMenuGroup(e.Menu, menu => {
            AddRunScriptMenuItem(menu, control);
            AddMarkdownTableMenuItem(menu, control);
            AddOpenLinkInWindowMenuItem(menu, control);
            AddMarkdownPathRefactorMenuItem(menu, control);
        });
        // ④ 版（Git）と実行（デバッグ）
        AddMenuGroup(e.Menu, menu => {
            AddGitMenuItems(menu, control);
            AddDebugMenuItems(menu, control);
        });
    }

    /// <summary>1つの束を足す。中身が1つでも入ったときだけ、束の<b>前</b>に区切り線を1本入れる。
    /// 寄稿側が自分で区切り線を足さなくなるので、「区切り線だけが並ぶ」「末尾が区切り線で終わる」が
    /// 構造的に起きない。</summary>
    internal static void AddMenuGroup(ContextMenu menu, Action<ContextMenu> build)
        => SelectionActionMenuBuilder.AddMenuGroup(menu, build);
    /// <summary>右クリック位置（＝キャレット位置。エディタは右クリックでキャレットを移す）にリンクがあれば
    /// 「別ウィンドウで開く」を足す。URL はブラウザの、ファイルはエディタの切り離しウィンドウで開く
    /// ——素材を別の面へ渡す既存の動線（切り離し）に、本文中のリンクからも入れるようにする。</summary>
    private void AddOpenLinkInWindowMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control is null)
            return;
        var target = SelectionActionTargetResolver.OpenLinkAtCaret(
            _workspace, control.Text, control.Caret.Line, control.Caret.Column, control.FilePath);
        if (SelectionActionMenuBuilder.BuildDetachedWindowLinkMenuItem(
                target, () => OpenLinkTargetInDetachedWindow(target)) is { } item)
            menu.Items.Add(item);
    }
    /// <summary>「別ウィンドウで開く」項目の見出し。括弧に宛先（ファイル名／ホスト名）を出して、
    /// どこが開くのかをメニューの時点で見せる。開けない宛先（未解決・フォルダー・mailto: 等）なら null。</summary>
    private static string? DescribeOpenLinkInWindow(LinkOpenTarget target)
        => SelectionActionPresentation.DetachedWindowLinkHeader(target);
    private void AddMarkdownTableMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path)
            return;
        if (SelectionActionMenuBuilder.BuildMarkdownTableMenuItem(
                path, control.Text, control.Caret.Line,
                () => EditMarkdownTable(control), () => InsertMarkdownTable(control)) is { } item)
            menu.Items.Add(item);
    }
    private void AddMarkdownPathRefactorMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } documentPath)
            return;
        if (SelectionActionMenuBuilder.BuildMarkdownPathRefactorMenuItem(
                _workspace, control.Text, control.Caret.Line, control.Caret.Column, documentPath,
                target => RefactorMarkdownLocalPath(
                    control, documentPath, target.LinkText, target.SourcePath, target.IsDirectory)) is { } item)
            menu.Items.Add(item);
    }

    private void RefactorMarkdownLocalPath(
        VimEditorControl control, string documentPath, string currentDestination,
        string sourcePath, bool isDirectory) {
        var destination = InputDialog.Prompt(
            this,
            "Markdown リンク先の移動",
            "この Markdown ファイルからの相対パスを入力してください。\n実体を移動し、同じ実体を指すリンクをすべて更新します。",
            currentDestination);
        if (destination is null)
            return;

        try {
            var result = MarkdownLocalLinkMoveCoordinator.Move(
                _workspace, documentPath, destination, sourcePath, isDirectory, control.Text,
                updatedText => {
                    control.SetText(updatedText);
                    control.Save(documentPath);
                },
                (from, to, directory) => _vm.FolderTree.NotifyEntryMoved(from, to, directory));
            if (!result.Moved) {
                ToastService.Info("移動先が現在のリンク先と同じです。");
                return;
            }
            ToastService.Success($"リンク先を {result.Destination} へ移動し、参照を更新しました。");
        } catch (Exception ex) {
            ToastService.Error($"リンク先を変更できませんでした: {ex.Message}");
        }
    }

    private void EditMarkdownTable(VimEditorControl control) {
        if (!MarkdownTableDocumentEditor.TryFindAtCaret(
                control.Text, control.Caret.Line, out var region))
            return;
        var edited = MarkdownTableGridWindow.Edit(this, region, _settings.Theme);
        if (edited is null)
            return;   // キャンセル
        control.SetText(MarkdownTableDocumentEditor.ReplaceTable(control.Text, region, edited));
    }
    private void InsertMarkdownTable(VimEditorControl control) {
        var edited = MarkdownTableGridWindow.Insert(this, _settings.Theme);
        if (edited is null)
            return;   // キャンセル
        var updatedText = MarkdownTableDocumentEditor.InsertTable(
            control.Text, control.Caret.Line, edited);
        if (updatedText is null)
            return;   // 何も入力せずに閉じた
        control.SetText(updatedText);
    }
    private void AddGitMenuItems(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path || !_vm.FolderTree.IsGitRepository)
            return;
        menu.Items.Add(SelectionActionMenuBuilder.BuildGitMenuItem(
            () => _ = ShowGitHistoryAsync(path), () => control.ExecuteCommand("Gblame")));
    }
    private void AddBlameCommitMenuItems(ContextMenu menu, VimEditorControl control, Editor.Controls.Git.EditorBlameLine blame) {
        SelectionActionMenuBuilder.AddBlameCommitMenuItems(
            menu, blame.CommitHash, () => ShowBlameCommitDiff(control, blame), () => {
            if (control.FilePath is { Length: > 0 } p)
                _ = ShowGitHistoryAsync(p, blame.CommitHash);
        });
    }
    private void ShowBlameCommitDiff(VimEditorControl control, Editor.Controls.Git.EditorBlameLine blame) {
        if (blame.CommitHash is not { Length: > 0 } hash) return;
        ShowDiff(new DiffOpenTarget.CommitFile(
            hash, $"コミット {DiffOpenTarget.Short(hash)}", control.FilePath, blame.OriginalLine));
    }
    private async Task ShowGitHistoryAsync(string fullPath, string? commitHash = null) {
        await _vm.GitSession.ShowPathHistoryAsync(Path.GetFullPath(fullPath), commitHash);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
        FocusPane(PaneKind.Git);
    }
    /// <summary>デバッグ操作は「デバッグ」サブメニュー1つに畳む（停止中は4項目まで増えるため）。
    /// <b>デバッガの管轄拡張子のファイルにだけ</b>出す——以前は拡張子を問わず出していたので、
    /// .md や .json を右クリックしても「ブレークポイントの条件を編集…」が並んでいた。</summary>
    private void AddDebugMenuItems(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path || !IsDebuggableSource(path)) return;
        var manager = ManagerForPath(path);
        var line0 = control.Caret.Line;
        menu.Items.Add(SelectionActionMenuBuilder.BuildDebugMenuItem(
            manager, path, line0, () => EditBreakpointCondition(path, line0)));
    }
    private void EditBreakpointCondition(string path, int line0) {
        var bps = ManagerForPath(path).Breakpoints;
        var current = bps.FindBreakpoint(path, line0)?.Condition ?? "";
        var input = InputDialog.Prompt(this, "ブレークポイントの条件", "条件式（真のとき停止。例: i > 5）。空にすると条件を解除します。", current, allowEmpty: true);
        if (input is null) return;  // キャンセル
        bps.EnsureBreakpoint(path, line0).Condition = input.Trim();
    }
    private void OnTerminalContextMenuBuilding(object? sender, TerminalContextMenuBuildingEventArgs e)
        // ターミナル側はエディタと違いホスト項目の区切り線を整理しないので、ここも束として足す
        // （中身が入ったときだけ、ネイティブ項目との間に区切り線が1本入る）。
        => AddMenuGroup(e.Menu, menu => SelectionActionMenuBuilder.AddSelectionMenuItems(
            menu, e.SelectedText, e.HasSelection,
            SelectionActionMenuBuilder.BuildEditorSendMenuItem(
                _workspace, e.SelectedText, e.HasSelection,
                (sender as TerminalTabView)?.WorkingDirectory, currentDocumentPath: null,
                openLocation: location => _ = SendLocationToEditorAsync(location)),
            SelectionActionMenuBuilder.BuildDiffSendMenu(
                CompareEntries(control: null, "ターミナルの選択", e.SelectedText, e.HasSelection)),
            !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
            () => {
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
                _vm.AiBar.AskAbout(e.SelectedText);
            },
            text => _ = SearchSelectionInBrowserAsync(text),
            AddWorkflowMenuItems));
    private async Task SendLocationToEditorAsync(SourceLocation location) {
        await OpenPathInEditorAsync(location.Path, location.Line, location.Column);
        FocusPane(PaneKind.Editor);
    }
    /// <summary>この右クリックで Diff へ送れる相手を並べる（選択範囲／ファイル全体／保存済みの内容）。</summary>
    private IReadOnlyList<SelectionCompareMenuEntry> CompareEntries(
        VimEditorControl? control, string sourceLabel, string selectedText, bool hasSelection) {
        var path = control?.FilePath ?? "";
        var presentations = SelectionActionPresentation.CompareEntries(
            sourceLabel, selectedText, hasSelection, control is not null, path, control?.IsModified == true);
        return presentations.Select(presentation => new SelectionCompareMenuEntry(
            presentation.Label,
            presentation.ToolTip,
            () => RunCompareEntry(presentation, control, path, selectedText))).ToList();
    }
    private void RunCompareEntry(
        SelectionComparePresentation presentation, VimEditorControl? control, string path, string selectedText) {
        ShowDiffResolution(DiffComparisonSourceResolver.FromSelection(
            presentation, selectedText, control, path, ClipboardText.TryGet));
    }
    /// <summary>エクスプローラーからの比較要求：ファイル同士、またはファイルとクリップボードを Diff ペインへ。
    /// 2ファイルのときは右（新側）を比較の出どころにして、行から飛ぶ先を「新しい方」に揃える。</summary>
    private void CompareFilesInDiff(FileCompareRequest request) {
        ShowDiffResolution(DiffComparisonSourceResolver.FromFiles(
            request.LeftPath, request.RightPath, ClipboardText.TryGet));
    }
    private void ShowDiffResolution(DiffComparisonResolution resolution) {
        if (resolution.ErrorMessage is { } error) {
            ToastService.Error(error);
            return;
        }
        if (resolution.Comparison is { } comparison)
            CompareInDiff(comparison);
    }
    /// <summary>比較を差分の行き先へ渡す（「〜へ送る」の共通の締め）。ペインが出ていればペインへ、
    /// 隠れていれば別ウィンドウへ——判断は <see cref="ShowDiff"/> ひとつが持つ。</summary>
    private void CompareInDiff(DiffComparison comparison)
        => ShowDiff(new DiffOpenTarget.Comparison(comparison));
    private void AddRunScriptMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path || !SelectionActionPolicy.IsRunnableScript(path))
            return;
        menu.Items.Add(SelectionActionMenuBuilder.BuildRunScriptMenuItem(
            path, _activeTerminalTab is not null, () => RunScriptInTerminal(control, path)));
    }
    private void RunScriptInTerminal(VimEditorControl control, string path) {
        if (control.IsModified) {
            try {
                control.Save(path);
            } catch (Exception ex) {
                ToastService.Error($"保存に失敗したため実行を中止しました: {ex.Message}");
                return;
            }
        }
        if (_activeTerminalTab?.View is not { } view)
            return;
        SetPaneVisible(PaneKind.Terminal, true);
        _ = view.RunCommandAsync($"& {FileDragDrop.PowerShellQuote(path)}", CancellationToken.None);
        FocusPane(PaneKind.Terminal);
    }
    private void AddWorkflowMenuItems(ContextMenu menu, string input) {
        var workflows = _vm.AiBar.Workflow.ListInputWorkflows().Select(workflow => (workflow.Id, workflow.Name));
        SelectionActionMenuBuilder.AddWorkflowMenuItems(
            menu, input, workflows, !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
            (id, value) => RunWorkflowWithInput(id, value));
    }
    private void RunWorkflowWithInput(string workflowId, string input)
        => RunWorkflowWithInput(workflowId, WorkflowRunInput.FromText(input));
    private void RunWorkflowWithInput(string workflowId, WorkflowRunInput input) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
        _vm.AiBar.Mode = AiBarMode.Workflow;
        _vm.AiBar.IsExpanded = true;
        _vm.AiBar.Workflow.RunWithInput(workflowId, input);
    }
    private async Task SearchSelectionInBrowserAsync(string selectedText) {
        if (SelectionActionPresentation.BrowserSearchTarget(selectedText) is not { } target)
            return;
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        await OpenUrlInBrowserAsync(target.Url, target.Title);
    }
}
