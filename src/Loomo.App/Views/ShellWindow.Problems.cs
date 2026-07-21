namespace sk0ya.Loomo.App.Views;

/// <summary>診断（Problems）パネルの集約。各エディタタブの LSP マネージャ（<see cref="IEditorLspManager"/>、
/// publishDiagnostics を既に受信・保持している）の <c>StateChanged</c> を購読し、変化のたびに全realized
/// タブの <c>VimEditorControl.EffectiveDiagnostics</c>（LSP＋ホスト診断を合成済み）を読み直して
/// <see cref="ViewModels.ProblemsPanelViewModel"/> へ渡す。ViewModel 側は表示用データを持つだけで
/// Editor.Controls には触れない（デバッグ連携の ShellWindow.Debug.cs と同じ「Viewが橋渡し」の流儀）。</summary>
public partial class ShellWindow
{
    private readonly HashSet<VimEditorControl> _problemsSubscribed = new();
    private DispatcherTimer? _problemsRefreshDebounce;

    private void InitializeProblemsWiring()
    {
        _problemsRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _problemsRefreshDebounce.Tick += (_, _) => { _problemsRefreshDebounce!.Stop(); RefreshProblems(); };
        _vm.Problems.OpenRequested += OnProblemOpenRequested;
    }

    /// <summary>タブが実体化したら、その LSP マネージャの診断変化を拾えるようにする（実体化のたびに1回だけ）。
    /// <see cref="RealizeEditorControl"/> から呼ぶ。</summary>
    private void EnsureProblemsSubscribed(EditorTab tab)
    {
        if (!tab.IsRealized || !_problemsSubscribed.Add(tab.Control))
            return;
        if (GetLspManager(tab) is { } lsp)
            lsp.StateChanged += ScheduleProblemsRefresh;
        ScheduleProblemsRefresh();
    }

    private void ScheduleProblemsRefresh()
    {
        _problemsRefreshDebounce?.Stop();
        _problemsRefreshDebounce?.Start();
    }

    /// <summary>開いている全realizedタブの診断を集約し直す。タブを閉じた分は毎回 <see cref="_editorTabs"/> から
    /// 素直に作り直すことで自然に落ちる（個別の後始末は不要）。</summary>
    private void RefreshProblems()
    {
        var items = new List<ProblemItemViewModel>();
        foreach (var tab in _editorTabs)
        {
            if (!tab.IsRealized) continue;
            var path = tab.PeekFilePath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            foreach (var d in tab.Control.EffectiveDiagnostics)
                items.Add(new ProblemItemViewModel(path, d));
        }
        _vm.Problems.ReplaceItems(items);
    }

    private void OnProblemOpenRequested(ProblemItemViewModel item)
        => _ = OpenPathInEditorAsync(item.FilePath, item.Line1, item.Column1);
}
