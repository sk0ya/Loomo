using System.IO;
using Editor.Controls.Rendering;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグマネージャの状態を実体化済みエディタへ反映する。</summary>
internal sealed class DebugEditorController
{
    private readonly DebugManagerViewModelBase _dotnet;
    private readonly DebugManagerViewModelBase _typescript;
    private readonly Func<IEnumerable<VimEditorControl>> _realizedEditors;
    private readonly Func<string, Task> _openEditorFile;
    private readonly Action<string, int> _previewFrame;
    private readonly Action<string, int> _activateFrame;
    private bool _attached;

    internal DebugEditorController(
        DebugManagerViewModelBase dotnet,
        DebugManagerViewModelBase typescript,
        Func<IEnumerable<VimEditorControl>> realizedEditors,
        Func<string, Task> openEditorFile,
        Action<string, int> previewFrame,
        Action<string, int> activateFrame)
    {
        _dotnet = dotnet;
        _typescript = typescript;
        _realizedEditors = realizedEditors;
        _openEditorFile = openEditorFile;
        _previewFrame = previewFrame;
        _activateFrame = activateFrame;
    }

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        AttachManager(_dotnet);
        AttachManager(_typescript);
    }

    internal DebugManagerViewModelBase ManagerForPath(string? path)
        => DebugSourcePolicy.IsTypeScriptSource(path) ? _typescript : _dotnet;

    internal void WireEditor(VimEditorControl control)
    {
        control.SetBreakpointsEnabled(true);
        control.BreakpointToggled += line => ToggleBreakpoint(control, line);
        // ファイルパスは後から変わり得るため、管轄マネージャは評価時に引く。
        control.DataTipEvaluator = (request, _) =>
            ManagerForPath(control.FilePath).Inspection?.EvaluateDataTipAsync(request.Expression)
            ?? Task.FromResult<string?>(null);
        control.SetDataTipsEnabled(ManagerForPath(control.FilePath).IsStopped);
        control.BufferChanged += (_, _) => SyncBreakpoints(control);
        SyncBreakpoints(control);
    }

    private void AttachManager(DebugManagerViewModelBase manager)
    {
        manager.ExecutionLineChanged += (path, line0) => OnExecutionLineChanged(manager, path, line0);
        manager.FramePreviewRequested += _previewFrame;
        manager.FrameActivated += _activateFrame;
        manager.BreakpointsRefreshed += OnBreakpointsRefreshed;
        manager.StoppedChanged += stopped => OnStoppedChanged(manager, stopped);
    }

    private IEnumerable<VimEditorControl> EditorsFor(DebugManagerViewModelBase manager)
        => _realizedEditors().Where(control => ReferenceEquals(ManagerForPath(control.FilePath), manager));

    private void OnStoppedChanged(DebugManagerViewModelBase manager, bool stopped)
    {
        foreach (var control in EditorsFor(manager))
            control.SetDataTipsEnabled(stopped);
    }

    private void OnBreakpointsRefreshed(string path)
    {
        if (FindEditor(path) is { } control)
            SyncBreakpoints(control);
    }

    private void ToggleBreakpoint(VimEditorControl control, int line0)
    {
        var path = control.FilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        ManagerForPath(path).Breakpoints.ToggleBreakpoint(path, line0);
        SyncBreakpoints(control);
    }

    private void SyncBreakpoints(VimEditorControl control)
    {
        var path = control.FilePath;
        control.SetBreakpoints(string.IsNullOrWhiteSpace(path)
            ? Array.Empty<EditorBreakpoint>()
            : ManagerForPath(path).Breakpoints.GetBreakpointGlyphs(path)
                .Select(DebugBreakpointMapper.ToEditorBreakpoint).ToList());
    }

    private async void OnExecutionLineChanged(
        DebugManagerViewModelBase manager, string? path, int line0)
    {
        foreach (var editor in EditorsFor(manager))
            editor.SetExecutionLine(-1);
        if (string.IsNullOrWhiteSpace(path) || line0 < 0) return;
        var control = FindEditor(path);
        if (control is null)
        {
            await _openEditorFile(path);
            control = FindEditor(path);
        }
        control?.SetExecutionLine(line0);
    }

    private VimEditorControl? FindEditor(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return _realizedEditors().FirstOrDefault(control =>
            !string.IsNullOrWhiteSpace(control.FilePath)
            && string.Equals(Path.GetFullPath(control.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
    }
}
