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
        _lspWorkspace.DiagnosticsPublished += OnLspDiagnosticsPublished;
        _workspace.FoldersChanged += OnProblemWorkspaceFoldersChanged;
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

    private void SelectProblemRange(ProblemItemViewModel item)
    {
        if (_activeEditorTab is not { IsRealized: true } tab ||
            !string.Equals(tab.Control.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        tab.Control.SelectRange(item.Line1 - 1, item.Column1 - 1,
            item.EndLine1 - 1, item.EndColumn1 - 1);
    }
}
