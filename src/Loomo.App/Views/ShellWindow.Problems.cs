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

    private void OnProblemOpenRequested(ProblemItemViewModel item)
        => _ = OpenPathInEditorAsync(item.FilePath, item.Line1, item.Column1);
}
