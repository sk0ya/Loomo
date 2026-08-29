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
        AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection,
            BuildEditorSendMenuItem(
                e.SelectedText, e.HasSelection, workingDirectory: null, control?.FilePath),
            BuildDiffSendMenu(CompareEntries(
                control, SelectionSourceLabel(control), e.SelectedText, e.HasSelection)));
        AddSemanticSelectionMenuItems(e.Menu, control);
        AddRefactorMenuItems(e.Menu, control);
        AddOpenLinkInWindowMenuItem(e.Menu, control);
        AddRunScriptMenuItem(e.Menu, control);
        AddGitMenuItems(e.Menu, control);
        AddDebugMenuItems(e.Menu, control);
        AddMarkdownTableMenuItem(e.Menu, control);
        AddMarkdownPathRefactorMenuItem(e.Menu, control);
    }
    /// <summary>右クリック位置（＝キャレット位置。エディタは右クリックでキャレットを移す）にリンクがあれば
    /// 「別ウィンドウで開く」を足す。URL はブラウザの、ファイルはエディタの切り離しウィンドウで開く
    /// ——素材を別の面へ渡す既存の動線（切り離し）に、本文中のリンクからも入れるようにする。</summary>
    private void AddOpenLinkInWindowMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control is null)
            return;
        var lines = control.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (control.Caret.Line < 0 || control.Caret.Line >= lines.Length
            || LinkDetector.FindLinkAt(lines[control.Caret.Line], control.Caret.Column) is not { } link)
            return;
        var target = LinkOpenTargetResolver.Resolve(_workspace, link.Text, control.FilePath);
        if (DescribeOpenLinkInWindow(target) is not { } header)
            return;
        menu.Items.Add(new Separator());
        var item = new MenuItem { Header = header, ToolTip = target.Value };
        item.Click += (_, _) => OpenLinkTargetInDetachedWindow(target);
        menu.Items.Add(item);
    }
    /// <summary>「別ウィンドウで開く」項目の見出し。括弧に宛先（ファイル名／ホスト名）を出して、
    /// どこが開くのかをメニューの時点で見せる。開けない宛先（未解決・フォルダー・mailto: 等）なら null。</summary>
    private static string? DescribeOpenLinkInWindow(LinkOpenTarget target) => target.Kind switch {
        LinkOpenTargetKind.Url => $"リンク先を別ウィンドウで開く（{UrlHost(target.Value)}）",
        LinkOpenTargetKind.File => $"リンク先を別ウィンドウで開く（{Path.GetFileName(target.Value)}）",
        _ => null,
    };
    private static string UrlHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } host
            ? host
            : "ブラウザ";
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
                _workspace, link.Text, documentPath,
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
                        _workspace, link.Text, documentPath,
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
        ShowDiff(new DiffOpenTarget.CommitFile(
            hash, $"コミット {DiffOpenTarget.Short(hash)}", control.FilePath, blame.OriginalLine));
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
        => AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection,
            BuildEditorSendMenuItem(
                e.SelectedText, e.HasSelection,
                (sender as TerminalTabView)?.WorkingDirectory, currentDocumentPath: null),
            BuildDiffSendMenu(CompareEntries(control: null, "ターミナルの選択", e.SelectedText, e.HasSelection)));
    /// <summary>選択テキストがファイルの場所（パス＋行・列）を指しているなら「エディタへ送る」1項目を作る。
    /// ビルドエラー・スタックトレース・grep 出力・Git の diff 見出しなど、
    /// <b>その場に出ている文字列</b>をそのまま宛先にして、その行へ着地させる（設計書 §23.3 の「〜へ送る」）。
    /// 読み取れない／実在しないときは null を返して<b>項目自体を出さない</b>
    /// （押せるのに何も起きない項目を作らないのがこの部屋の作法。<see cref="DescribeOpenLinkInWindow"/> と同じ）。</summary>
    private MenuItem? BuildEditorSendMenuItem(
        string selectedText, bool hasSelection, string? workingDirectory, string? currentDocumentPath) {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText))
            return null;
        if (!SourceLocationResolver.TryResolve(
                _workspace, selectedText, workingDirectory, currentDocumentPath, out var location))
            return null;
        var name = Path.GetFileName(location.Path);
        var where = location.Line > 0 ? $"{name}:{location.Line}" : name;
        var item = new MenuItem {
            Header = $"エディタへ送る（{where}）",
            ToolTip = location.Line > 0 ? $"{location.Path}:{location.Line}" : location.Path, };
        item.Click += (_, _) => _ = SendLocationToEditorAsync(location);
        return item;
    }
    private async Task SendLocationToEditorAsync(SourceLocation location) {
        await OpenPathInEditorAsync(location.Path, location.Line, location.Column);
        FocusPane(PaneKind.Editor);
    }
    /// <summary>選択テキストの出どころの呼び名（比較の左右見出しに使う）。</summary>
    private static string SelectionSourceLabel(VimEditorControl? control)
        => control?.FilePath is { Length: > 0 } path
            ? $"選択範囲（{Path.GetFileName(path)}）"
            : "エディタの選択";
    /// <summary>選択テキストを他のペインへ渡す項目群。<paramref name="diffItem"/> は
    /// <see cref="BuildDiffSendMenu"/> が作った「Diffへ送る」1項目で、他の「〜へ送る」と同じ高さに並べる
    /// （選択が無くてもファイル比較だけは出したいので、この項目だけは選択の有無に依らず受け取る）。
    /// <paramref name="editorItem"/> は <see cref="BuildEditorSendMenuItem"/> が作った「エディタへ送る」で、
    /// 選択がファイルの場所として読めたときだけ非 null になる。</summary>
    private void AddSelectionMenuItems(
        ContextMenu menu, string selectedText, bool hasSelection,
        MenuItem? editorItem, MenuItem? diffItem) {
        var hasText = hasSelection && !string.IsNullOrWhiteSpace(selectedText);
        if (!hasText && editorItem is null && diffItem is null)
            return;
        menu.Items.Add(new Separator());
        if (hasText) {
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
        }
        if (editorItem is not null)
            menu.Items.Add(editorItem);
        if (diffItem is not null)
            menu.Items.Add(diffItem);
        if (hasText)
            AddWorkflowMenuItems(menu, selectedText);
    }
    /// <summary>Diff ペインへ送れる比較の行き先1つぶん。</summary>
    private readonly record struct CompareEntry(string Label, string ToolTip, Action Run);
    /// <summary>この右クリックで Diff へ送れる相手を並べる（選択範囲／ファイル全体／保存済みの内容）。</summary>
    private IReadOnlyList<CompareEntry> CompareEntries(
        VimEditorControl? control, string sourceLabel, string selectedText, bool hasSelection) {
        var entries = new List<CompareEntry>();
        var path = control?.FilePath ?? "";
        if (hasSelection && !string.IsNullOrWhiteSpace(selectedText))
            entries.Add(new CompareEntry(
                "選択範囲とクリップボードを比較",
                "選択テキストを左、クリップボードの内容を右に置いて Diff ペインで見比べる",
                () => CompareWithClipboard(sourceLabel, selectedText, path)));
        if (control is null)
            return entries;
        var name = path.Length > 0 ? Path.GetFileName(path) : "エディタの内容";
        entries.Add(new CompareEntry(
            "ファイル全体とクリップボードを比較",
            "エディタの内容を左、クリップボードの内容を右に置いて Diff ペインで見比べる",
            () => CompareWithClipboard(name, control.Text, path)));
        if (path.Length > 0 && control.IsModified && File.Exists(path))
            entries.Add(new CompareEntry(
                "保存済みの内容と比較（未保存の変更）",
                "ディスク上の保存済みの内容と、編集中の内容の差分を Diff ペインで見る",
                () => CompareEditorWithSavedFile(control, path, name)));
        return entries;
    }
    /// <summary>
    /// 比較の入口を「Diffへ送る」<b>1項目</b>にまとめる。選択範囲とファイルで別々に「Diffへ送る」を出すと、
    /// 同じ名前が2つ並んでどちらが何を送るのか読めない（設計書 §24.3 の「送る」は宛先1つにつき1項目）。
    /// 行き先が複数あるときだけ子メニューにし、1つしか無いとき（ターミナル＝ファイルが無い）は平のままにする。
    /// </summary>
    private static MenuItem? BuildDiffSendMenu(IReadOnlyList<CompareEntry> entries) {
        if (entries.Count == 0)
            return null;
        if (entries.Count == 1) {
            var only = entries[0];
            var flat = new MenuItem { Header = $"Diffへ送る（{only.Label}）", ToolTip = only.ToolTip };
            flat.Click += (_, _) => only.Run();
            return flat;
        }
        var parent = new MenuItem {
            Header = "Diffへ送る", ToolTip = "クリップボードや保存済みの内容と Diff ペインで見比べる", };
        foreach (var entry in entries) {
            var item = new MenuItem { Header = entry.Label, ToolTip = entry.ToolTip };
            item.Click += (_, _) => entry.Run();
            parent.Items.Add(item);
        }
        return parent;
    }
    private void CompareEditorWithSavedFile(VimEditorControl control, string path, string name) {
        try {
            // 右＝編集中のバッファがエディタで見えている版なので、行の対応は右側で引く（既定）。
            CompareInDiff(new DiffComparison(
                $"{name}（保存済み）", File.ReadAllText(path), $"{name}（編集中）", control.Text, path));
        } catch (Exception ex) {
            ToastService.Error($"保存済みの内容を読めませんでした: {ex.Message}");
        }
    }
    /// <summary>素材を左、今のクリップボードを右に置いて比較する。クリップボードが空なら何もせず知らせる
    /// （空文字と比べた「全部削除」の差分を見せても意味がないため）。</summary>
    private void CompareWithClipboard(string leftTitle, string leftText, string filePath) {
        if (ClipboardText.TryGet() is not { } clipboard) {
            ToastService.Error("クリップボードにテキストがありません。");
            return;
        }
        // 左がそのファイル由来（選択範囲・ファイル全体）なので、行の対応は左側で引く。
        CompareInDiff(new DiffComparison(
            leftTitle, leftText, "クリップボード", clipboard, filePath, FileIsLeft: true));
    }
    /// <summary>エクスプローラーからの比較要求：ファイル同士、またはファイルとクリップボードを Diff ペインへ。
    /// 2ファイルのときは右（新側）を比較の出どころにして、行から飛ぶ先を「新しい方」に揃える。</summary>
    private void CompareFilesInDiff(FileCompareRequest request) {
        try {
            if (BinaryFileDetector.IsBinary(request.LeftPath)
                || (request.RightPath is { } binaryCheck && BinaryFileDetector.IsBinary(binaryCheck))) {
                ToastService.Error("バイナリファイルは比較できません。");
                return;
            }
            var leftName = Path.GetFileName(request.LeftPath);
            var leftText = File.ReadAllText(request.LeftPath);
            if (request.RightPath is { Length: > 0 } rightPath) {
                CompareInDiff(new DiffComparison(
                    leftName, leftText, Path.GetFileName(rightPath), File.ReadAllText(rightPath), rightPath));
                return;
            }
            if (ClipboardText.TryGet() is not { } clipboard) {
                ToastService.Error("クリップボードにテキストがありません。");
                return;
            }
            CompareInDiff(new DiffComparison(
                leftName, leftText, "クリップボード", clipboard, request.LeftPath, FileIsLeft: true));
        } catch (Exception ex) {
            ToastService.Error($"ファイルを読めませんでした: {ex.Message}");
        }
    }
    /// <summary>比較を差分の行き先へ渡す（「〜へ送る」の共通の締め）。ペインが出ていればペインへ、
    /// 隠れていれば別ウィンドウへ——判断は <see cref="ShowDiff"/> ひとつが持つ。</summary>
    private void CompareInDiff(DiffComparison comparison)
        => ShowDiff(new DiffOpenTarget.Comparison(comparison));
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
