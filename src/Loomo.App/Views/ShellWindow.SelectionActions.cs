using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Editor.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ターミナル／エディタの選択テキストに対する右クリックアクション
/// （「AIに聞く」＝AIバーへ即送信、「ブラウザで調べる」＝内蔵ブラウザでBing検索）。
/// メニュー項目はライブラリ側の ContextMenuBuilding フックで各コントロールのネイティブメニュー末尾へ
/// 追加する（選択があるときだけ。スタイルはライブラリが自前のメニュー様式に合わせる）。
/// </summary>
public partial class ShellWindow
{
    // 検索クエリ／タブ名に使う最大長。長すぎる選択はここで切り詰める。
    private const int MaxSearchQueryLength = 300;

    private void OnEditorContextMenuBuilding(object? sender, EditorContextMenuBuildingEventArgs e)
    {
        AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection);
        var control = sender as VimEditorControl ?? _activeEditorTab?.Control;
        // 右クリックされたエディタ（複数分割でもイベント発火元）で開いているスクリプトを実行する項目を足す。
        AddRunScriptMenuItem(e.Menu, control);
        // Git リポジトリ配下で開いているファイルなら「Git」>「Git Blame」を足す。
        AddGitMenuItems(e.Menu, control);
        // デバッグ停止中なら、カーソル行に対するデバッグ操作（次のステートメントに設定）を足す。
        AddDebugMenuItems(e.Menu, control);
        // .md でカーソルが Markdown テーブル内にあるなら「テーブルを VGrid で編集」を足す。
        AddMarkdownTableMenuItem(e.Menu, control);
    }

    private static readonly string[] MarkdownExtensions = { ".md", ".markdown" };

    // カーソルが Markdown テーブル内にあるとき、そのテーブルを VGrid グリッドのウィンドウで編集する項目を足す。
    // .md/.markdown 以外・パス無し（未保存の新規バッファ）・テーブル外では何もしない。
    private void AddMarkdownTableMenuItem(ContextMenu menu, VimEditorControl? control)
    {
        if (control?.FilePath is not { Length: > 0 } path || !IsMarkdownFile(path))
            return;

        var lines = control.Text.Replace("\r\n", "\n").Split('\n');
        if (!MarkdownTableSync.TryFindTableAt(lines, control.Caret.Line, out _))
            return;

        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "テーブルを VGrid で編集…" };
        edit.Click += (_, _) => EditMarkdownTable(control);
        menu.Items.Add(edit);
    }

    private static bool IsMarkdownFile(string path)
        => Array.Exists(
            MarkdownExtensions,
            ext => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase));

    // カーソル位置の Markdown テーブルをグリッドウィンドウで編集し、閉じたら本文の該当行を再生成テーブルへ差し替える。
    // クリック時点の本文で再検出する（メニュー表示後に本文が変わっていても取りこぼさない）。
    private void EditMarkdownTable(VimEditorControl control)
    {
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

    // カーソル位置のファイルに対する Git 操作をメニュー末尾へ足す（今のところ Git Blame のみ、
    // 今後 Git Status/Diff/Log 等も同じ「Git」親メニューへ増やす想定）。ファイルが開かれていて、
    // かつ現在のワークスペースが Git リポジトリのときだけ出す（FolderTree と同じ判定基準を使う）。
    // Blame の実処理はエディタ側（VimEditorControl の :Gblame。GitServiceFactory で渡している
    // GitDiffProvider が git blame を実行しインライン注釈を出す）にすべて任せ、ここでは
    // ExecuteCommand でトリガーするだけでよい。
    private void AddGitMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (control?.FilePath is not { Length: > 0 } || !_vm.FolderTree.IsGitRepository)
            return;

        menu.Items.Add(new Separator());
        var git = new MenuItem { Header = "Git" };
        var blame = new MenuItem { Header = "Git Blame" };
        blame.Click += (_, _) => control.ExecuteCommand("Gblame");
        git.Items.Add(blame);
        menu.Items.Add(git);
    }

    // カーソル行に対するデバッグ操作をメニュー末尾へ足す。ブレークポイント条件編集は常時、Run to Cursor／
    // 次のステートメント設定は停止中のみ。対象は右クリックされたエディタの開いているファイル。
    private void AddDebugMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (control?.FilePath is not { Length: > 0 } path) return;
        var dbg = _vm.Debug;
        var line0 = control.Caret.Line;  // 0 始まり

        menu.Items.Add(new Separator());

        // ブレークポイントの条件編集（停止中でなくても設定できる）。
        var editCond = new MenuItem { Header = "ブレークポイントの条件を編集…" };
        editCond.Click += (_, _) => EditBreakpointCondition(path, line0);
        menu.Items.Add(editCond);

        // 停止中のみ：カーソル行まで実行／次のステートメントに設定。
        if (dbg.IsStopped)
        {
            var runTo = new MenuItem { Header = "カーソル行まで実行" };
            runTo.Click += (_, _) => _ = dbg.Launch.RunToCursorAsync(path, line0);
            menu.Items.Add(runTo);

            if (dbg.Launch.SupportsSetNextStatement)
            {
                var setNext = new MenuItem { Header = "次のステートメントに設定（この行へ）" };
                setNext.Click += (_, _) => _ = dbg.Launch.SetNextStatementAsync(path, line0);
                menu.Items.Add(setNext);
            }

            // 「特定の関数にステップ イン」：候補は停止行依存なので、サブメニューを開いた時点で取得して並べる。
            if (dbg.Launch.SupportsStepInTargets)
                menu.Items.Add(BuildStepInTargetsMenu(dbg.Launch));
        }
    }

    // ステップ イン候補を遅延（サブメニュー展開時）に取得して並べる親メニュー項目を作る。
    private static MenuItem BuildStepInTargetsMenu(ViewModels.DebugLaunchViewModel dbg)
    {
        var parent = new MenuItem { Header = "特定の関数にステップ イン" };
        parent.Items.Add(new MenuItem { Header = "(読み込み中…)", IsEnabled = false });
        parent.SubmenuOpened += async (_, _) =>
        {
            var targets = await dbg.GetStepInTargetsAsync();
            parent.Items.Clear();
            if (targets.Count == 0)
            {
                parent.Items.Add(new MenuItem { Header = "(候補がありません)", IsEnabled = false });
                return;
            }
            foreach (var t in targets)
            {
                var item = new MenuItem { Header = t.Label };
                item.Click += (_, _) => _ = dbg.StepIntoTargetAsync(t);
                parent.Items.Add(item);
            }
        };
        return parent;
    }

    // カーソル行のブレークポイント条件を編集する。既存条件を初期値にし、空入力で条件を解除する
    // （その行にブレークポイントが無ければ作成して条件を付ける）。
    private void EditBreakpointCondition(string path, int line0)
    {
        var bps = _vm.Debug.Breakpoints;
        var current = bps.FindBreakpoint(path, line0)?.Condition ?? "";
        var input = InputDialog.Prompt(this, "ブレークポイントの条件",
            "条件式（真のとき停止。例: i > 5）。空にすると条件を解除します。",
            current, allowEmpty: true);
        if (input is null) return;  // キャンセル
        bps.EnsureBreakpoint(path, line0).Condition = input.Trim();
    }

    private void OnTerminalContextMenuBuilding(object? sender, TerminalContextMenuBuildingEventArgs e)
        => AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection);

    // 選択テキストに対する「AIに聞く」「ブラウザで調べる」をメニュー末尾へ足す。選択が無ければ何もしない。
    private void AddSelectionMenuItems(ContextMenu menu, string selectedText, bool hasSelection)
    {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText))
            return;

        menu.Items.Add(new Separator());

        var ask = new MenuItem
        {
            Header = "AIに聞く",
            // 処理中・暖機中は送信できないので無効化（押しても AskAbout 側で弾かれるが見た目も合わせる）。
            IsEnabled = !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
        };
        ask.Click += (_, _) =>
        {
            // AIペインがレイアウトに無ければ左上と入れ替えて表示してから送信する。
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
            _vm.AiBar.AskAbout(selectedText);
        };
        menu.Items.Add(ask);

        var search = new MenuItem { Header = "ブラウザで調べる" };
        search.Click += (_, _) => _ = SearchSelectionInBrowserAsync(selectedText);
        menu.Items.Add(search);

        AddWorkflowMenuItems(menu, selectedText);
    }

    // 実行できるスクリプト拡張子（PowerShell / バッチ）。
    private static readonly string[] RunnableScriptExtensions = { ".ps1", ".bat", ".cmd" };

    // エディタで開いている .ps1/.bat/.cmd を可視ターミナルで実行する項目を足す。
    // スクリプト以外・パス無し（未保存の新規バッファ）では何もしない。
    private void AddRunScriptMenuItem(ContextMenu menu, VimEditorControl? control)
    {
        if (control?.FilePath is not { Length: > 0 } path || !IsRunnableScript(path))
            return;

        menu.Items.Add(new Separator());
        var run = new MenuItem
        {
            Header = $"ターミナルで実行（{Path.GetFileName(path)}）",
            // ターミナルが無ければ実行先が無いので無効化する。
            IsEnabled = _activeTerminalTab is not null,
        };
        run.Click += (_, _) => RunScriptInTerminal(control, path);
        menu.Items.Add(run);
    }

    private static bool IsRunnableScript(string path)
        => Array.Exists(
            RunnableScriptExtensions,
            ext => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase));

    // 開いているスクリプトを可視ターミナルで実行する。編集中なら先に保存し、ディスク上の最新内容を走らせる。
    private void RunScriptInTerminal(VimEditorControl control, string path)
    {
        if (control.IsModified)
        {
            try
            {
                control.Save(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"保存に失敗したため実行を中止しました: {ex.Message}",
                    "ターミナルで実行", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (_activeTerminalTab?.View is not { } view)
            return;

        SetPaneVisible(PaneKind.Terminal, true);
        // 呼び出し演算子（&）でパスを実行する。空白を含むパスに備えて引用する。
        _ = view.RunCommandAsync($"& \"{path}\"", CancellationToken.None);
        FocusPane(PaneKind.Terminal);
    }

    // 選択テキストを {{input}} として実行する「AIワークフロー」サブメニューを足す。
    // 入力ありワークフローが無ければ何も足さない。処理中・暖機中は無効化する。
    private void AddWorkflowMenuItems(ContextMenu menu, string input)
    {
        var workflows = _vm.AiBar.Workflow.ListInputWorkflows();
        if (workflows.Count == 0)
            return;

        var parent = new MenuItem
        {
            Header = "AIワークフロー",
            IsEnabled = !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
        };
        foreach (var wf in workflows)
        {
            var id = wf.Id;
            var item = new MenuItem { Header = wf.Name };
            item.Click += (_, _) => RunWorkflowWithInput(id, input);
            parent.Items.Add(item);
        }
        menu.Items.Add(parent);
    }

    // AIペインをワークフローモードで前面に出し、指定ワークフローを構造化 input で実行する。
    // FolderTree／エディタのコンテキストメニュー双方の合流点。
    private void RunWorkflowWithInput(string workflowId, string input)
        => RunWorkflowWithInput(workflowId, WorkflowRunInput.FromText(input));

    private void RunWorkflowWithInput(string workflowId, WorkflowRunInput input)
    {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
        _vm.AiBar.Mode = AiBarMode.Workflow;
        _vm.AiBar.IsExpanded = true;
        _vm.AiBar.Workflow.RunWithInput(workflowId, input);
    }

    // 選択テキストを Bing で検索して内蔵ブラウザの新規タブで開く。
    private async Task SearchSelectionInBrowserAsync(string selectedText)
    {
        var query = BuildSearchQuery(selectedText);
        if (string.IsNullOrWhiteSpace(query))
            return;

        // ブラウザペインがレイアウトに無ければ左上と入れ替えて表示してから開く
        // （OpenUrlInBrowserAsync 側の表示処理は、ここで可視化済みなら何もしない）。
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);

        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
        await OpenUrlInBrowserAsync(url, $"検索: {query}");
    }

    // 改行・連続空白を 1 つの空白へまとめ、長すぎるクエリは切り詰める（URL・タブ名の暴発防止）。
    private static string BuildSearchQuery(string text)
    {
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Length > MaxSearchQueryLength
            ? collapsed[..MaxSearchQueryLength]
            : collapsed;
    }
}
