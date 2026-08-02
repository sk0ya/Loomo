using sk0ya.Loomo.Core.Markdown;
using sk0ya.Loomo.Core.Files;
using Editor.Core.Text;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナル／エディタの選択テキストに対する右クリックアクション （「AIへ送る」＝AIバーへ即送信、「ブラウザへ送る」＝内蔵ブラウザでBing検索）。 素材を別のペインへ渡す操作はすべて「〜へ送る」で揃える（設計書 §23.3 の共通語彙）。 メニュー項目はライブラリ側の ContextMenuBuilding フックで各コントロールのネイティブメニュー末尾へ 追加する（選択があるときだけ。スタイルはライブラリが自前のメニュー様式に合わせる）。</summary>
public partial class ShellWindow {
    private const int MaxSearchQueryLength = 300;
    private void OnEditorContextMenuBuilding(object? sender, EditorContextMenuBuildingEventArgs e) {
        var control = sender as VimEditorControl ?? _activeEditorTab?.Control;
        if (e.BlameLine is { } blame && control is not null) {
            AddBlameCommitMenuItems(e.Menu, control, blame);
            return;
        }
        AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection);
        AddRunScriptMenuItem(e.Menu, control);
        AddGitMenuItems(e.Menu, control);
        AddDebugMenuItems(e.Menu, control);
        AddMarkdownTableMenuItem(e.Menu, control);
        AddMarkdownPathRefactorMenuItem(e.Menu, control);
    }
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown" };
    private void AddMarkdownTableMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path || !IsMarkdownFile(path))
            return;
        var lines = control.Text.Replace("\r\n", "\n").Split('\n');
        bool inTable = MarkdownTableSync.TryFindTableAt(lines, control.Caret.Line, out _);
        menu.Items.Add(new Separator());
        if (inTable) {
            var edit = new MenuItem { Header = "テーブルを VGrid で編集…" };
            edit.Click += (_, _) => EditMarkdownTable(control);
            menu.Items.Add(edit);
        } else {
            var insert = new MenuItem { Header = "テーブルを挿入…" };
            insert.Click += (_, _) => InsertMarkdownTable(control);
            menu.Items.Add(insert);
        }
    }
    private static bool IsMarkdownFile(string path)
        => Array.Exists( MarkdownExtensions, ext => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase));

    private void AddMarkdownPathRefactorMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } documentPath || !IsMarkdownFile(documentPath))
            return;
        var lines = control.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (control.Caret.Line < 0 || control.Caret.Line >= lines.Length
            || LinkDetector.FindLinkAt(lines[control.Caret.Line], control.Caret.Column)
                is not { Kind: LinkKind.FilePath } link
            || !FileLinkResolver.TryResolve(
                link.Text, documentPath, _workspace.RootPath,
                out var sourcePath, out _, out _, out var isDirectory))
            return;

        menu.Items.Add(new Separator());
        var item = new MenuItem { Header = $"リンク先を移動・参照を更新…（{Path.GetFileName(sourcePath)}）" };
        item.Click += (_, _) => RefactorMarkdownLocalPath(
            control, documentPath, link.Text, sourcePath, isDirectory);
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

        destination = destination.Trim().Replace('\\', '/');
        var markdownDestination = destination.Replace(" ", "%20", StringComparison.Ordinal);
        try {
            if (Path.IsPathRooted(destination))
                throw new InvalidOperationException("ワークスペース内の相対パスを入力してください。");

            sourcePath = _workspace.ResolvePath(sourcePath);
            var documentDirectory = Path.GetDirectoryName(documentPath)
                ?? throw new InvalidOperationException("編集中の Markdown ファイルの場所を取得できません。");
            var destinationPath = _workspace.ResolvePath(Path.Combine(
                documentDirectory,
                Uri.UnescapeDataString(destination).Replace('/', Path.DirectorySeparatorChar)));
            if (PathsEqual(sourcePath, documentPath))
                throw new InvalidOperationException("編集中の Markdown ファイル自身はこの操作では移動できません。");
            if (PathsEqual(sourcePath, destinationPath)) {
                ToastService.Info("移動先が現在のリンク先と同じです。");
                return;
            }
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                throw new InvalidOperationException("移動先には同名のファイルまたはフォルダーが既にあります。");
            if (Path.GetDirectoryName(destinationPath) is not { } parent)
                throw new InvalidOperationException("移動先のフォルダーを取得できません。");

            var oldText = control.Text;
            var newText = ReplaceDetectedFileLinks(oldText, documentPath, sourcePath, markdownDestination);
            var createdDirectories = CreateMissingDirectories(parent);

            try {
                if (isDirectory)
                    Directory.Move(sourcePath, destinationPath);
                else
                    File.Move(sourcePath, destinationPath);
                control.SetText(newText);
                control.Save(documentPath);
            } catch {
                if (isDirectory && Directory.Exists(destinationPath))
                    Directory.Move(destinationPath, sourcePath);
                else if (!isDirectory && File.Exists(destinationPath))
                    File.Move(destinationPath, sourcePath);
                control.SetText(oldText);
                RemoveCreatedDirectories(createdDirectories);
                throw;
            }

            _vm.FolderTree.NotifyEntryMoved(sourcePath, destinationPath, isDirectory);
            ToastService.Success($"リンク先を {destination} へ移動し、参照を更新しました。");
        } catch (Exception ex) {
            ToastService.Error($"リンク先を変更できませんでした: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> CreateMissingDirectories(string directory) {
        var missing = new List<string>();
        for (var current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current)!) {
            if (string.IsNullOrWhiteSpace(current))
                throw new InvalidOperationException("移動先のフォルダーを作成できません。");
            missing.Add(current);
        }
        Directory.CreateDirectory(directory);
        return missing;
    }

    private static void RemoveCreatedDirectories(IReadOnlyList<string> directories) {
        foreach (var directory in directories) {
            try {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            } catch {
                // 元のファイル移動／保存エラーを優先する。
            }
        }
    }

    private string ReplaceDetectedFileLinks(
        string text, string documentPath, string sourcePath, string destination) {
        var replacements = new List<(int Start, int Length)>();
        var offset = 0;
        foreach (var line in text.Split('\n')) {
            foreach (var link in LinkDetector.FindLinks(line)) {
                if (link.Kind == LinkKind.FilePath
                    && FileLinkResolver.TryResolve(
                        link.Text, documentPath, _workspace.RootPath,
                        out var resolved, out _, out _, out _)
                    && PathsEqual(resolved, sourcePath))
                    replacements.Add((offset + link.Start, link.End - link.Start));
            }
            offset += line.Length + 1;
        }
        foreach (var replacement in replacements.OrderByDescending(x => x.Start))
            text = text.Remove(replacement.Start, replacement.Length)
                .Insert(replacement.Start, destination);
        return text;
    }
    private void EditMarkdownTable(VimEditorControl control) {
        var newline = control.Text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = control.Text.Replace("\r\n", "\n").Split('\n');
        if (!MarkdownTableSync.TryFindTableAt(lines, control.Caret.Line, out var region))
            return;
        var edited = MarkdownTableGridWindow.Edit(this, region, _settings.Theme);
        if (edited is null)
            return;   // キャンセル
        var table = MarkdownTableSync.SerializeTable(edited, region.Alignments);
        var result = new System.Collections.Generic.List<string>(lines.Length);
        result.AddRange(lines[..region.StartLine]);
        if (table.Length > 0)
            result.AddRange(table.Split('\n'));
        result.AddRange(lines[(region.EndLine + 1)..]);
        control.SetText(string.Join(newline, result));
    }
    private void InsertMarkdownTable(VimEditorControl control) {
        var edited = MarkdownTableGridWindow.Insert(this, _settings.Theme);
        if (edited is null)
            return;   // キャンセル
        var table = MarkdownTableSync.SerializeTable(edited, Array.Empty<MarkdownColumnAlignment>());
        if (table.Length == 0)
            return;   // 何も入力せずに閉じた
        var newline = control.Text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = control.Text.Replace("\r\n", "\n").Split('\n');
        var result = MarkdownTableSync.InsertTableAt(lines, control.Caret.Line, table);
        control.SetText(string.Join(newline, result));
    }
    private void AddGitMenuItems(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path || !_vm.FolderTree.IsGitRepository)
            return;
        menu.Items.Add(new Separator());
        var git = new MenuItem { Header = "Git" };
        var history = new MenuItem { Header = "履歴を表示" };
        history.Click += (_, _) => _ = ShowGitHistoryAsync(path);
        git.Items.Add(history);
        var blame = new MenuItem { Header = "Git Blame" };
        blame.Click += (_, _) => control.ExecuteCommand("Gblame");
        git.Items.Add(blame);
        menu.Items.Add(git);
    }
    private void AddBlameCommitMenuItems( ContextMenu menu, VimEditorControl control, Editor.Controls.Git.EditorBlameLine blame) {
        var shortHash = blame.CommitHash is { Length: > 7 } h ? h[..7] : blame.CommitHash;
        var diff = new MenuItem { Header = $"Diff で差分を表示（{shortHash}）" };
        diff.Click += (_, _) => ShowBlameCommitDiff(control, blame);
        menu.Items.Add(diff);
        var history = new MenuItem { Header = "Git ペインでこのファイルの履歴を表示" };
        history.Click += (_, _) => {
            if (control.FilePath is { Length: > 0 } p)
                _ = ShowGitHistoryAsync(p, blame.CommitHash);
        };
        menu.Items.Add(history);
    }
    private void ShowBlameCommitDiff(VimEditorControl control, Editor.Controls.Git.EditorBlameLine blame) {
        if (blame.CommitHash is not { Length: > 0 } hash) return;
        _ = _vm.DiffSession.ShowCommitFileAsync( hash, $"コミット {hash}", control.FilePath, blame.OriginalLine);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
        FocusPane(PaneKind.Diff);
    }
    private async Task ShowGitHistoryAsync(string fullPath, string? commitHash = null) {
        await _vm.GitSession.ShowPathHistoryAsync(Path.GetFullPath(fullPath), commitHash);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
        FocusPane(PaneKind.Git);
    }
    private void AddDebugMenuItems(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path) return;
        // ファイルの管轄マネージャ（.ts/.js 系→TS IDE、それ以外→dotnet IDE）へ振り分ける。
        var mgr = ManagerForPath(path);
        var line0 = control.Caret.Line;  // 0 始まり
        menu.Items.Add(new Separator());
        var editCond = new MenuItem { Header = "ブレークポイントの条件を編集…" };
        editCond.Click += (_, _) => EditBreakpointCondition(path, line0);
        menu.Items.Add(editCond);
        if (mgr.IsStopped) {
            var runTo = new MenuItem { Header = "カーソル行まで実行" };
            runTo.Click += (_, _) => _ = mgr switch {
                ViewModels.TsDebugViewModel ts => ts.Launch.RunToCursorAsync(path, line0),
                ViewModels.DebugViewModel d => d.Launch.RunToCursorAsync(path, line0),
                _ => Task.CompletedTask,
            };
            menu.Items.Add(runTo);
            // 次のステートメント設定・特定関数へのステップインは dotnet（netcoredbg）のみ対応。
            if (mgr is ViewModels.DebugViewModel dbg) {
                if (dbg.Launch.SupportsSetNextStatement) {
                    var setNext = new MenuItem { Header = "次のステートメントに設定（この行へ）" };
                    setNext.Click += (_, _) => _ = dbg.Launch.SetNextStatementAsync(path, line0);
                    menu.Items.Add(setNext);
                }
                if (dbg.Launch.SupportsStepInTargets)
                    menu.Items.Add(BuildStepInTargetsMenu(dbg.Launch));
            }
        }
    }
    private static MenuItem BuildStepInTargetsMenu(ViewModels.DebugLaunchViewModel dbg) {
        var parent = new MenuItem { Header = "特定の関数にステップ イン" };
        parent.Items.Add(new MenuItem { Header = "(読み込み中…)", IsEnabled = false });
        parent.SubmenuOpened += async (_, _) => {
            var targets = await dbg.GetStepInTargetsAsync();
            parent.Items.Clear();
            if (targets.Count == 0) {
                parent.Items.Add(new MenuItem { Header = "(候補がありません)", IsEnabled = false });
                return;
            }
            foreach (var t in targets) {
                var item = new MenuItem { Header = t.Label };
                item.Click += (_, _) => _ = dbg.StepIntoTargetAsync(t);
                parent.Items.Add(item);
            }
        };
        return parent;
    }
    private void EditBreakpointCondition(string path, int line0) {
        var bps = ManagerForPath(path).Breakpoints;
        var current = bps.FindBreakpoint(path, line0)?.Condition ?? "";
        var input = InputDialog.Prompt(this, "ブレークポイントの条件", "条件式（真のとき停止。例: i > 5）。空にすると条件を解除します。", current, allowEmpty: true);
        if (input is null) return;  // キャンセル
        bps.EnsureBreakpoint(path, line0).Condition = input.Trim();
    }
    private void OnTerminalContextMenuBuilding(object? sender, TerminalContextMenuBuildingEventArgs e)
        => AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection);
    private void AddSelectionMenuItems(ContextMenu menu, string selectedText, bool hasSelection) {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText))
            return;
        menu.Items.Add(new Separator());
        var ask = new MenuItem {
            Header = "AIへ送る", ToolTip = "選択テキストについてAIに尋ねる",
            IsEnabled = !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp, };
        ask.Click += (_, _) => {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
            _vm.AiBar.AskAbout(selectedText);
        };
        menu.Items.Add(ask);
        var search = new MenuItem {
            Header = "ブラウザへ送る", ToolTip = "選択テキストをブラウザペインで検索する", };
        search.Click += (_, _) => _ = SearchSelectionInBrowserAsync(selectedText);
        menu.Items.Add(search);
        AddWorkflowMenuItems(menu, selectedText);
    }
    private static readonly string[] RunnableScriptExtensions = { ".ps1", ".bat", ".cmd" };
    private void AddRunScriptMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path || !IsRunnableScript(path))
            return;
        menu.Items.Add(new Separator());
        var run = new MenuItem {
            Header = $"ターミナルで実行（{Path.GetFileName(path)}）", IsEnabled = _activeTerminalTab is not null, };
        run.Click += (_, _) => RunScriptInTerminal(control, path);
        menu.Items.Add(run);
    }
    private static bool IsRunnableScript(string path)
        => Array.Exists( RunnableScriptExtensions, ext => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase));
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
        _ = view.RunCommandAsync($"& \"{path}\"", CancellationToken.None);
        FocusPane(PaneKind.Terminal);
    }
    private void AddWorkflowMenuItems(ContextMenu menu, string input) {
        var workflows = _vm.AiBar.Workflow.ListInputWorkflows();
        if (workflows.Count == 0)
            return;
        var parent = new MenuItem {
            Header = "AIワークフローへ送る", ToolTip = "選択テキストを入力にしてワークフローを実行する",
            IsEnabled = !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp, };
        foreach (var wf in workflows) {
            var id = wf.Id;
            var item = new MenuItem { Header = wf.Name };
            item.Click += (_, _) => RunWorkflowWithInput(id, input);
            parent.Items.Add(item);
        }
        menu.Items.Add(parent);
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
        var query = BuildSearchQuery(selectedText);
        if (string.IsNullOrWhiteSpace(query))
            return;
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
        await OpenUrlInBrowserAsync(url, $"検索: {query}");
    }
    private static string BuildSearchQuery(string text) {
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Length > MaxSearchQueryLength
            ? collapsed[..MaxSearchQueryLength]
            : collapsed;
    }
}
