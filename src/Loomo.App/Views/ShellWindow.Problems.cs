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
    }

    private void OnProblemOpenRequested(ProblemItemViewModel item)
        => _ = OpenPathInEditorAsync(item.FilePath, item.Line1, item.Column1);
}
