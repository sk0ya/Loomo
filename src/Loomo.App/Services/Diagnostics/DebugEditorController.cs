using System.IO;
using Editor.Controls.Rendering;
using sk0ya.Loomo.Core.Debug;

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

    // 行末の値（Inline Values）。マネージャごとの最新の一揃いと、編集で値が古くなったので消したエディタ。
    private readonly Dictionary<DebugManagerViewModelBase, DebugInlineValueSet> _inlineValues = new();
    private readonly HashSet<VimEditorControl> _inlineValuesStale = new();

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
        control.BufferChanged += (_, _) => OnEditorBufferChanged(control);
        SyncBreakpoints(control);
        SyncInlineValues(control);
    }

    /// <summary>エディタにファイルが載った後（<c>LoadFile</c> は行末の値を捨てるが <c>BufferChanged</c> を出さない）。
    /// 停止と同時に開いたタブにも、すでに組み上がっている値を出す。</summary>
    internal void OnEditorFileLoaded(VimEditorControl control)
    {
        _inlineValuesStale.Remove(control);
        SyncInlineValues(control);
    }

    private void AttachManager(DebugManagerViewModelBase manager)
    {
        manager.ExecutionLineChanged += (path, line0) => OnExecutionLineChanged(manager, path, line0);
        manager.FramePreviewRequested += _previewFrame;
        manager.FrameActivated += _activateFrame;
        manager.BreakpointsRefreshed += OnBreakpointsRefreshed;
        manager.StoppedChanged += stopped => OnStoppedChanged(manager, stopped);
        manager.InlineValuesChanged += values => OnInlineValuesChanged(manager, values);
    }

    // --- 行末の値（Inline Values） ---

    private void OnInlineValuesChanged(DebugManagerViewModelBase manager, DebugInlineValueSet values)
    {
        _inlineValues[manager] = values;
        // 新しい停止（または続行による消去）が来たら、編集で消していた分も含めて出し直す。
        _inlineValuesStale.Clear();
        foreach (var control in EditorsFor(manager))
            SyncInlineValues(control);
    }

    /// <summary>そのエディタのファイルに該当する値だけを出す（無ければ消す）。</summary>
    private void SyncInlineValues(VimEditorControl control)
    {
        var manager = ManagerForPath(control.FilePath);
        var lines = _inlineValuesStale.Contains(control) || !_inlineValues.TryGetValue(manager, out var values)
            ? Array.Empty<DebugInlineValueLine>()
            : values.LinesFor(control.FilePath);
        ApplyInlineValues(control, lines);
    }

    /// <summary>停止中にソースを編集したら、そのエディタの値は消す（行がずれて別の行の値に見えるため）。
    /// 次の停止で出し直す。</summary>
    private void OnEditorBufferChanged(VimEditorControl control)
    {
        if (!_inlineValues.TryGetValue(ManagerForPath(control.FilePath), out var values)
            || values.LinesFor(control.FilePath).Count == 0
            || !_inlineValuesStale.Add(control))
            return;
        ApplyInlineValues(control, Array.Empty<DebugInlineValueLine>());
    }

    /// <summary>エディタへ描かせる。描画 API（<c>VimEditorControl.SetInlineValues</c>）は
    /// sk0ya.Editor.Controls 1.0.95 から。古いピンでは収集・無効化までで、描画は行わない（Loomo.App.csproj 参照）。</summary>
    private static void ApplyInlineValues(VimEditorControl control, IReadOnlyList<DebugInlineValueLine> lines)
    {
#if LOOMO_EDITOR_INLINE_VALUES
        control.SetInlineValues(lines.Select(l => new EditorInlineValue(l.Line0, l.Text)).ToList());
#else
        _ = control;
        _ = lines;
#endif
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
