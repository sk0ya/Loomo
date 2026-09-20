using sk0ya.Loomo.CSharp.Configuration;

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
        if (Editor.Core.Lsp.LspUri.TryToLocalPath(uri) is { } localPath &&
            string.Equals(Path.GetExtension(localPath), ".cs", StringComparison.OrdinalIgnoreCase) &&
            _editorTabs.Any(tab => tab.IsRealized && tab.Control.FilePath is { Length: > 0 } editorPath &&
                string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(localPath),
                    StringComparison.OrdinalIgnoreCase)))
        {
            // 開いているC#文書はEditorDiagnosticSessionから同じ版の診断を一括反映する。
            return;
        }

        var presentationDiagnostics = ExpandUnnecessaryUsingDiagnostics(uri, diagnostics);
        _vm.Debug.Problems.SetLspDiagnostics(uri, presentationDiagnostics);
        _vm.TsIde.Problems.SetLspDiagnostics(uri, presentationDiagnostics);
    }

    private IReadOnlyList<Editor.Core.Lsp.LspDiagnostic> ExpandUnnecessaryUsingDiagnostics(
        string uri, IReadOnlyList<Editor.Core.Lsp.LspDiagnostic> diagnostics)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var documentUri) || !documentUri.IsFile ||
            !string.Equals(Path.GetExtension(documentUri.LocalPath), ".cs", StringComparison.OrdinalIgnoreCase))
            return diagnostics;

        var path = Path.GetFullPath(documentUri.LocalPath);
        var tab = _editorTabs.FirstOrDefault(candidate => candidate.IsRealized &&
            string.Equals(Path.GetFullPath(candidate.Control.FilePath ?? ""), path,
                StringComparison.OrdinalIgnoreCase));
        if (tab is null)
            return diagnostics;

        var individualRanges = _compilerUnusedUsingRanges.GetValueOrDefault(tab.Control) ?? [];
        return CSharpDiagnosticMerger.ExpandUnnecessaryUsingGroups(diagnostics, individualRanges);
    }

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
