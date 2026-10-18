namespace sk0ya.Loomo.App.Views;

/// <summary>IDE ペイン「問題」タブとエディタの橋渡し。中身（ビルド出力のパース）は
/// <see cref="ViewModels.ProblemsViewModel"/> 側で完結しており（流し込みは各ビルド実行箇所が
/// <c>IDebugSession.ReportBuildOutput</c> で行う）、ここは行クリックでのジャンプだけを配線する。</summary>
public partial class ShellWindow
{
    private void InitializeProblemsWiring()
    {
        _vm.Debug.Problems.OpenRequested += OnProblemOpenRequested;
        _vm.TsIde.Problems.OpenRequested += OnProblemOpenRequested;
        _vm.Debug.Problems.QuickFixRequested += OnProblemQuickFixRequested;
        _vm.TsIde.Problems.QuickFixRequested += OnProblemQuickFixRequested;
        _vm.Debug.Problems.ExcerptRequested += OnProblemExcerptRequested;
        _vm.TsIde.Problems.ExcerptRequested += OnProblemExcerptRequested;
        _lspWorkspace.DiagnosticsPublished += OnLspDiagnosticsPublished;
        _workspace.FoldersChanged += OnProblemWorkspaceFoldersChanged;
        _vm.Debug.Problems.Changed += ScheduleProblemMarks;
        _vm.TsIde.Problems.Changed += ScheduleProblemMarks;
        // コミットの前に、そのファイルのエラーを確かめる（§24.34）。押した時点の数で見る（印の更新を待たない）。
        _vm.GitPanel.ProblemLookup = path => CurrentProblemCounts().For(path);
        // パッチを当てる前の確認（§24.38.1）。
        _vm.GitPanel.ConfirmPatch = (title, message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private bool _problemMarksPending;

    /// <summary>いまの問題の数（IDE と TS IDE の Problems を合わせたもの・§24.33）。コミット前の確認（§24.34）も引く。</summary>
    private ProblemFileCounts CurrentProblemCounts()
        => ProblemFileCounts.From(_vm.Debug.Problems.AllProblems().Concat(_vm.TsIde.Problems.AllProblems()));

    /// <summary>ツリーとタブの問題の印を付け直す（§24.33）。診断はキー入力ごとに届くので、続けて来た分は
    /// 1 回にまとめ、入力と描画より後（Background）で数える。</summary>
    private void ScheduleProblemMarks()
    {
        if (_problemMarksPending) return;
        _problemMarksPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
            _problemMarksPending = false;
            var counts = CurrentProblemCounts();
            _vm.FolderTree.ApplyProblemCounts(counts);
            _vm.Tabs.ApplyProblemCounts(counts);
        }));
    }

    private void OnLspDiagnosticsPublished(string uri, IReadOnlyList<Editor.Core.Lsp.LspDiagnostic> diagnostics)
        => Dispatcher.BeginInvoke(new Action(() => PublishLspDiagnosticsToProblems(uri, diagnostics)));

    private void PublishLspDiagnosticsToProblems(
        string uri, IReadOnlyList<Editor.Core.Lsp.LspDiagnostic> diagnostics)
    {
        // 開いているC#文書は EditorDiagnosticSession が正本。ここで別途流し込むと、同じ診断が
        // 束ねられた姿（IDE0005のグループ）と1本ずつに割った姿の両方でProblemsに並ぶ。
        // 見るのはタブ一覧ではなくセッション一覧——分割・切り離しのエディタもここに載る。
        if (Editor.Core.Lsp.LspUri.TryToLocalPath(uri) is { } localPath &&
            string.Equals(Path.GetExtension(localPath), ".cs", StringComparison.OrdinalIgnoreCase) &&
            _diagnosticSessions.Values.Any(session => session.FilePath is { Length: > 0 } ownedPath &&
                string.Equals(ownedPath, Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase)))
            return;

        _vm.Debug.Problems.SetLspDiagnostics(uri, diagnostics);
        _vm.TsIde.Problems.SetLspDiagnostics(uri, diagnostics);
    }

    private void OnProblemWorkspaceFoldersChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => {
            _vm.Debug.Problems.ClearLspDiagnostics();
            _vm.TsIde.Problems.ClearLspDiagnostics();
            // タスクの結果（§31.17）は前のワークスペースのファイルを指しているので捨てる。走り続けている watch の
            // 出力も前のワークスペースのものなので、追うのをやめる（コマンドそのものは人間の端末に残す）。
            _taskRunner?.StopWatching();
            _vm.Debug.Problems.ClearTaskProblems();
            _vm.TsIde.Problems.ClearTaskProblems();
            // 開いたままのエディタぶんも一度消して、今のスナップショットから出し直す。
            // 消すだけだと、ワークスペースを切り替えても開き続けている文書の問題が空欄になる。
            _vm.Debug.Problems.ClearAllEditorDiagnostics();
            _vm.TsIde.Problems.ClearAllEditorDiagnostics();
            foreach (var control in _diagnosticSessions.Keys.ToArray())
                RefreshStyleCopPresentation(control);
        }));

    private async void OnProblemOpenRequested(ProblemItemViewModel item)
    {
        await OpenPathInEditorAsync(item.FilePath, item.Line1, item.Column1);
        SelectProblemRange(item);
    }

    private async void OnProblemQuickFixRequested(ProblemItemViewModel item)
    {
        await OpenPathInEditorAsync(item.FilePath, item.Line1, item.Column1);
        SelectProblemRange(item);
        if (_activeEditorTab is { IsRealized: true } tab &&
            string.Equals(tab.Control.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            tab.Control.ExecuteCommand("QuickFix");
    }

    /// <summary>問題一覧から抜粋タブ（§35.3）を開く。どの行を流すか（表示中・同じコード・1ファイル）は
    /// <see cref="ProblemExcerpts"/> が決めており、ここは開くだけ。</summary>
    private void OnProblemExcerptRequested(ProblemExcerptPlan plan)
    {
        if (plan.Requests.Count == 0)
        {
            ShowRefactorStatus(plan.OutsideCount > 0
                ? $"抜粋にできる問題がありません（ワークスペースの外の {plan.OutsideCount} 件は抜粋にしません）。"
                : "抜粋にできる問題がありません。");
            return;
        }
        OpenExcerptTab(plan.Title, plan.Requests,
            plan.OutsideCount > 0 ? $"ワークスペースの外の {plan.OutsideCount} 件は除きました。" : null);
    }

    private void SelectProblemRange(ProblemItemViewModel item)
    {
        if (_activeEditorTab is not { IsRealized: true } tab ||
            !string.Equals(tab.Control.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        tab.Control.SelectRange(item.Line1 - 1, item.Column1 - 1,
            item.EndLine1 - 1, item.EndColumn1 - 1);
    }
}
