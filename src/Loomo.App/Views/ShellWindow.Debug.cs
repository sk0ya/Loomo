
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// デバッグ（DAP）とエディタの橋渡し。エディタのブレークポイント列のトグルを
/// <see cref="ViewModels.DebugViewModel"/> のブレークポイントストア＋デバッグアダプタへ反映し、
/// 停止位置（実行中行）をエディタのハイライトへ反映する。
/// ブレークポイント状態の真実は DebugViewModel が持ち、エディタは表示するだけ（AI/人間で経路を一本化する流儀）。
/// </summary>
public partial class ShellWindow
{
    // ctor から一度だけ呼び、停止位置→実行行ハイライトの配線をつなぐ。 デバッグペインの開閉はタイトルバーのペイントグル（他ペインと同じ OnTogglePaneVisibility）が担う。 アダプタ未導入の再確認は未導入バーの「再確認」ボタンで行う。
    private void InitializeDebugWiring()
    {
        _vm.Debug.ExecutionLineChanged += OnDebugExecutionLineChanged;
        _vm.Debug.FramePreviewRequested += OnDebugFramePreviewRequested;
        _vm.Debug.FrameActivated += OnDebugFrameActivated;
        _vm.Debug.BreakpointsRefreshed += OnDebugBreakpointsRefreshed;
        _vm.Debug.StoppedChanged += OnDebugStoppedChanged;
    }

    // 停止/再開に合わせて全エディタの DataTip（ホバー値表示）を有効化/無効化する。 停止中だけホバー評価を許可し、再開・終了でポップアップを閉じる。
    private void OnDebugStoppedChanged(bool stopped)
    {
        foreach (var c in RealizedEditorControls())
            c.SetDataTipsEnabled(stopped);
    }

    // ブレークポイント管理パネルでの削除/全削除を、そのパスを開いているエディタのガターへ反映する。
    private void OnDebugBreakpointsRefreshed(string path)
    {
        if (FindEditorControl(path) is { } control) SyncEditorBreakpoints(control);
    }

    // コールスタックのフレーム選択：そのソース位置をプレビュータブで表示し、その行へスクロールする。 フォーカスはデバッグペイン側に残す（プレビュー）。
    private async void OnDebugFramePreviewRequested(string path, int line0)
    {
        await OpenFileInPreviewTabAsync(path);
        NavigateActiveEditorTo(path, line0);
    }

    // コールスタックのフレームのダブルクリック：通常タブで開いてジャンプし、エディタにフォーカスする。
    private async void OnDebugFrameActivated(string path, int line0)
    {
        // OpenPathInEditorAsync は 1 始まりの行を受け取り内部で -1 する。
        await OpenPathInEditorAsync(path, line0 + 1, column: 0);
        if (_activeEditorTab is { } tab) tab.Control.Focus();
    }

    // アクティブなエディタタブが指定パスを開いていれば、その行（0 始まり）へキャレットを移す。
    private void NavigateActiveEditorTo(string path, int line0)
    {
        if (line0 < 0) return;
        var full = Path.GetFullPath(path);
        if (_activeEditorTab is { } tab && !string.IsNullOrWhiteSpace(tab.PeekFilePath) &&
            string.Equals(Path.GetFullPath(tab.PeekFilePath), full, StringComparison.OrdinalIgnoreCase))
            tab.Control.NavigateTo(line0, 0);
    }

    // 新規エディタにブレークポイント列を有効化し、トグル/同期を配線する（BuildEditorControl から呼ぶ）。
    private void WireEditorForDebug(VimEditorControl control)
    {
        control.SetBreakpointsEnabled(true);
        control.BreakpointToggled += line => OnEditorBreakpointToggled(control, line);
        // 停止中はホバーで式を評価して値を表示する（DataTip）。停止していなければ VM 側で null を返す。
        control.DataTipEvaluator = (req, _) => _vm.Debug.Inspection.EvaluateDataTipAsync(req.Expression);
        control.SetDataTipsEnabled(_vm.Debug.IsStopped);
        // ファイル読込・差し替え時に、そのパスのブレークポイントをエディタへ反映する。
        control.BufferChanged += (_, _) => SyncEditorBreakpoints(control);
        SyncEditorBreakpoints(control);
    }

    private void OnEditorBreakpointToggled(VimEditorControl control, int line0)
    {
        var path = control.FilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _vm.Debug.Breakpoints.ToggleBreakpoint(path, line0);
        SyncEditorBreakpoints(control);
    }

    private void SyncEditorBreakpoints(VimEditorControl control)
    {
        var path = control.FilePath;
        control.SetBreakpoints(string.IsNullOrWhiteSpace(path)
            ? Array.Empty<EditorBreakpoint>()
            : _vm.Debug.Breakpoints.GetBreakpointGlyphs(path).Select(ToEditorBreakpoint).ToList());
    }

    // VM のブレークポイントメタを Editor のガター表示用 EditorBreakpoint へ写像する。 ログポイント（◆）を条件付き（＋）より優先して描く。
    private static EditorBreakpoint ToEditorBreakpoint(BreakpointGlyphInfo info)
    {
        var glyph = info.IsLogpoint ? BreakpointGlyphKind.Logpoint
                  : info.HasCondition ? BreakpointGlyphKind.Conditional
                  : BreakpointGlyphKind.Normal;
        return new EditorBreakpoint(info.Line0, glyph, info.Enabled);
    }

    // 停止位置をエディタへ反映する。まず全エディタの実行行を解除し、対象パスのエディタを （無ければ開いて）見つけてその行をハイライトする。path=null/line<0 は解除のみ。
    private async void OnDebugExecutionLineChanged(string? path, int line0)
    {
        foreach (var c in RealizedEditorControls())
            c.SetExecutionLine(-1);

        if (string.IsNullOrWhiteSpace(path) || line0 < 0) return;

        var control = FindEditorControl(path);
        if (control is null)
        {
            await OpenFileInNewEditorTabAsync(path);
            control = FindEditorControl(path);
        }
        control?.SetExecutionLine(line0);
    }

    private IEnumerable<VimEditorControl> RealizedEditorControls()
        => _editorTabs.Where(t => t.IsRealized).Select(t => t.Control);

    private VimEditorControl? FindEditorControl(string path)
    {
        var full = Path.GetFullPath(path);
        return RealizedEditorControls().FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.FilePath) &&
            string.Equals(Path.GetFullPath(c.FilePath), full, StringComparison.OrdinalIgnoreCase));
    }
}
