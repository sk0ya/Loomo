
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// デバッグ（DAP）とエディタの橋渡し。エディタのブレークポイント列のトグルを
/// <see cref="ViewModels.DebugViewModel"/> のブレークポイントストア＋デバッグアダプタへ反映し、
/// 停止位置（実行中行）をエディタのハイライトへ反映する。
/// ブレークポイント状態の真実は DebugViewModel が持ち、エディタは表示するだけ（AI/人間で経路を一本化する流儀）。
/// </summary>
public partial class ShellWindow {
    private void InitializeDebugWiring() {
        _vm.Debug.ExecutionLineChanged += OnDebugExecutionLineChanged;
        _vm.Debug.FramePreviewRequested += OnDebugFramePreviewRequested;
        _vm.Debug.FrameActivated += OnDebugFrameActivated;
        _vm.Debug.BreakpointsRefreshed += OnDebugBreakpointsRefreshed;
        _vm.Debug.StoppedChanged += OnDebugStoppedChanged;
    }

    private void OnDebugStoppedChanged(bool stopped) {
        foreach (var c in RealizedEditorControls())
            c.SetDataTipsEnabled(stopped);
    }

    private void OnDebugBreakpointsRefreshed(string path) {
        if (FindEditorControl(path) is { } control) SyncEditorBreakpoints(control);
    }

    private async void OnDebugFramePreviewRequested(string path, int line0) {
        await OpenFileInPreviewTabAsync(path);
        NavigateActiveEditorTo(path, line0);
    }

    private async void OnDebugFrameActivated(string path, int line0) {
        await OpenPathInEditorAsync(path, line0 + 1, column: 0);
        if (_activeEditorTab is { } tab) tab.Control.Focus();
    }

    private void NavigateActiveEditorTo(string path, int line0) {
        if (line0 < 0) return;
        var full = Path.GetFullPath(path);
        if (_activeEditorTab is { } tab && !string.IsNullOrWhiteSpace(tab.PeekFilePath) &&
            string.Equals(Path.GetFullPath(tab.PeekFilePath), full, StringComparison.OrdinalIgnoreCase))
            tab.Control.NavigateTo(line0, 0);
    }

    private void WireEditorForDebug(VimEditorControl control) {
        control.SetBreakpointsEnabled(true);
        control.BreakpointToggled += line => OnEditorBreakpointToggled(control, line);
        control.DataTipEvaluator = (req, _) => _vm.Debug.Inspection.EvaluateDataTipAsync(req.Expression);
        control.SetDataTipsEnabled(_vm.Debug.IsStopped);
        control.BufferChanged += (_, _) => SyncEditorBreakpoints(control);
        SyncEditorBreakpoints(control);
    }

    private void OnEditorBreakpointToggled(VimEditorControl control, int line0) {
        var path = control.FilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _vm.Debug.Breakpoints.ToggleBreakpoint(path, line0);
        SyncEditorBreakpoints(control);
    }

    private void SyncEditorBreakpoints(VimEditorControl control) {
        var path = control.FilePath;
        control.SetBreakpoints(string.IsNullOrWhiteSpace(path)
            ? Array.Empty<EditorBreakpoint>()
            : _vm.Debug.Breakpoints.GetBreakpointGlyphs(path).Select(ToEditorBreakpoint).ToList());
    }

    private static EditorBreakpoint ToEditorBreakpoint(BreakpointGlyphInfo info) {
        var glyph = info.IsLogpoint ? BreakpointGlyphKind.Logpoint
                  : info.HasCondition ? BreakpointGlyphKind.Conditional
                  : BreakpointGlyphKind.Normal;
        return new EditorBreakpoint(info.Line0, glyph, info.Enabled);
    }

    private async void OnDebugExecutionLineChanged(string? path, int line0) {
        foreach (var c in RealizedEditorControls())
            c.SetExecutionLine(-1);

        if (string.IsNullOrWhiteSpace(path) || line0 < 0) return;

        var control = FindEditorControl(path);
        if (control is null) {
            await OpenFileInNewEditorTabAsync(path);
            control = FindEditorControl(path);
        }
        control?.SetExecutionLine(line0);
    }

    private IEnumerable<VimEditorControl> RealizedEditorControls()
        => _editorTabs.Where(t => t.IsRealized).Select(t => t.Control);

    private VimEditorControl? FindEditorControl(string path) {
        var full = Path.GetFullPath(path);
        return RealizedEditorControls().FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.FilePath) &&
            string.Equals(Path.GetFullPath(c.FilePath), full, StringComparison.OrdinalIgnoreCase));
    }
}
