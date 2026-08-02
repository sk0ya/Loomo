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
        => Dispatcher.BeginInvoke(new Action(() => {
            _vm.Debug.Problems.SetLspDiagnostics(uri, diagnostics);
            _vm.TsIde.Problems.SetLspDiagnostics(uri, diagnostics);
        }));

    private void OnProblemWorkspaceFoldersChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => {
            _vm.Debug.Problems.ClearLspDiagnostics();
            _vm.TsIde.Problems.ClearLspDiagnostics();
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
            tab.Control.ExecuteCommand("CodeAction");
    }

    private void SelectProblemRange(ProblemItemViewModel item)
    {
        if (_activeEditorTab is not { IsRealized: true } tab ||
            !string.Equals(tab.Control.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        tab.Control.SelectRange(item.Line1 - 1, item.Column1 - 1,
            item.EndLine1 - 1, item.EndColumn1 - 1);
    }
}
