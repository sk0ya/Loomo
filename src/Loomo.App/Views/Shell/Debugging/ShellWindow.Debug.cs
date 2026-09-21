namespace sk0ya.Loomo.App.Views;

/// <summary>デバッグ状態とShell上のエディタを結び付ける。</summary>
public partial class ShellWindow
{
    private DebugEditorController _debugEditorController = null!;

    internal static bool IsDebuggableSource(string? path)
        => DebugSourcePolicy.IsDebuggableSource(path);

    private void InitializeDebugWiring()
    {
        _debugEditorController = new DebugEditorController(
            _vm.Debug, _vm.TsIde, RealizedEditorControls,
            OpenFileInNewEditorTabAsync, OnDebugFramePreviewRequested, OnDebugFrameActivated);
        _debugEditorController.Attach();
    }

    private DebugManagerViewModelBase ManagerForPath(string? path)
        => _debugEditorController.ManagerForPath(path);

    private async void OnDebugFramePreviewRequested(string path, int line0)
    {
        await OpenFileInPreviewTabAsync(path);
        NavigateActiveEditorTo(path, line0);
    }

    private async void OnDebugFrameActivated(string path, int line0)
    {
        await OpenPathInEditorAsync(path, line0 + 1, column: 0);
        if (_activeEditorTab is { } tab) tab.Control.Focus();
    }

    private void NavigateActiveEditorTo(string path, int line0)
    {
        if (line0 < 0) return;
        var full = Path.GetFullPath(path);
        if (_activeEditorTab is { } tab && !string.IsNullOrWhiteSpace(tab.PeekFilePath)
            && string.Equals(Path.GetFullPath(tab.PeekFilePath), full, StringComparison.OrdinalIgnoreCase))
            tab.Control.NavigateTo(line0, 0);
    }

    private void WireEditorForDebug(VimEditorControl control)
        => _debugEditorController.WireEditor(control);

    private IEnumerable<VimEditorControl> RealizedEditorControls()
        => _editorTabs.Where(tab => tab.IsRealized).Select(tab => tab.Control);
}
