namespace sk0ya.Loomo.App.Views;
/// <summary>デバッグ（DAP）とエディタの橋渡し。エディタのブレークポイント列のトグルをブレークポイントストア＋デバッグアダプタへ反映し、 停止位置（実行中行）をエディタのハイライトへ反映する。 ブレークポイント状態の真実はマネージャ VM が持ち、エディタは表示するだけ（AI/人間で経路を一本化する流儀）。
/// マネージャは 2 つ（dotnet=<see cref="ViewModels.DebugViewModel"/> / TypeScript=<see cref="ViewModels.TsDebugViewModel"/>）あり、ファイルの拡張子で管轄を振り分ける（.ts/.js 系→TS、それ以外→dotnet。同じファイルが両方に属することはないため混線しない）。 実行行クリアや DataTip 有効化のような「全エディタ」操作は、発生源マネージャの管轄拡張子のエディタに限定する（両デバッガ同時実行時に潰し合わないように）。</summary>
public partial class ShellWindow {
    private static readonly string[] TsDebugExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];
    private void InitializeDebugWiring() {
        _vm.Debug.ExecutionLineChanged += (path, line0) => OnDebugExecutionLineChanged(_vm.Debug, path, line0);
        _vm.Debug.FramePreviewRequested += OnDebugFramePreviewRequested;
        _vm.Debug.FrameActivated += OnDebugFrameActivated;
        _vm.Debug.BreakpointsRefreshed += OnDebugBreakpointsRefreshed;
        _vm.Debug.StoppedChanged += stopped => OnDebugStoppedChanged(_vm.Debug, stopped);
        _vm.TsIde.ExecutionLineChanged += (path, line0) => OnDebugExecutionLineChanged(_vm.TsIde, path, line0);
        _vm.TsIde.FramePreviewRequested += OnDebugFramePreviewRequested;
        _vm.TsIde.FrameActivated += OnDebugFrameActivated;
        _vm.TsIde.BreakpointsRefreshed += OnDebugBreakpointsRefreshed;
        _vm.TsIde.StoppedChanged += stopped => OnDebugStoppedChanged(_vm.TsIde, stopped);
    }
    /// <summary>そのファイルパスを管轄するデバッグマネージャ（.ts/.js 系→TS IDE、それ以外→dotnet IDE）。</summary>
    private ViewModels.DebugManagerViewModelBase ManagerForPath(string? path) {
        if (!string.IsNullOrWhiteSpace(path)) {
            var ext = Path.GetExtension(path);
            if (TsDebugExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                return _vm.TsIde;
        }
        return _vm.Debug;
    }
    /// <summary>そのマネージャの管轄拡張子を持つ実体化済みエディタ（「全エディタ」操作の対象を管轄に限定する）。</summary>
    private IEnumerable<VimEditorControl> EditorControlsOf(ViewModels.DebugManagerViewModelBase manager)
        => RealizedEditorControls().Where(c => ReferenceEquals(ManagerForPath(c.FilePath), manager));
    private void OnDebugStoppedChanged(ViewModels.DebugManagerViewModelBase manager, bool stopped) {
        foreach (var c in EditorControlsOf(manager))
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
        if (_activeEditorTab is { } tab && !string.IsNullOrWhiteSpace(tab.PeekFilePath) && string.Equals(Path.GetFullPath(tab.PeekFilePath), full, StringComparison.OrdinalIgnoreCase))
            tab.Control.NavigateTo(line0, 0);
    }
    private void WireEditorForDebug(VimEditorControl control) {
        control.SetBreakpointsEnabled(true);
        control.BreakpointToggled += line => OnEditorBreakpointToggled(control, line);
        // ファイルパスは後から変わり得る（BufferChanged）ため、管轄マネージャは評価時に引く。
        control.DataTipEvaluator = (req, _) => ManagerForPath(control.FilePath).Inspection?.EvaluateDataTipAsync(req.Expression) ?? Task.FromResult<string?>(null);
        control.SetDataTipsEnabled(ManagerForPath(control.FilePath).IsStopped);
        control.BufferChanged += (_, _) => SyncEditorBreakpoints(control);
        SyncEditorBreakpoints(control);
    }
    private void OnEditorBreakpointToggled(VimEditorControl control, int line0) {
        var path = control.FilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        ManagerForPath(path).Breakpoints.ToggleBreakpoint(path, line0);
        SyncEditorBreakpoints(control);
    }
    private void SyncEditorBreakpoints(VimEditorControl control) {
        var path = control.FilePath;
        control.SetBreakpoints(string.IsNullOrWhiteSpace(path)
            ? Array.Empty<EditorBreakpoint>()
            : ManagerForPath(path).Breakpoints.GetBreakpointGlyphs(path).Select(ToEditorBreakpoint).ToList());
    }
    private static EditorBreakpoint ToEditorBreakpoint(BreakpointGlyphInfo info) {
        var glyph = info.IsLogpoint ? BreakpointGlyphKind.Logpoint
                  : info.HasCondition ? BreakpointGlyphKind.Conditional
                  : BreakpointGlyphKind.Normal;
        return new EditorBreakpoint(info.Line0, glyph, info.Enabled);
    }
    private async void OnDebugExecutionLineChanged(ViewModels.DebugManagerViewModelBase manager, string? path, int line0) {
        foreach (var c in EditorControlsOf(manager))
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
            !string.IsNullOrWhiteSpace(c.FilePath) && string.Equals(Path.GetFullPath(c.FilePath), full, StringComparison.OrdinalIgnoreCase));
    }
}
