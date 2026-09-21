using Editor.Controls.Rendering;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタのテスト／カバレッジグリフを同期し、ガターやキャレットからテストを実行する。</summary>
internal sealed class EditorTestGlyphController
{
    private readonly IWorkspaceService _workspace;
    private readonly Func<string?, ITestExplorer> _resolveExplorer;
    private readonly Func<string?, IReadOnlyList<EditorCoverageMarker>> _coverageMarkersForPath;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _showInfo;
    private readonly Action<string> _showError;
    private readonly List<WeakReference<VimEditorControl>> _editors = new();
    private readonly HashSet<VimEditorControl> _syncPending = new();
    private readonly EditorTestGlyphColumns _columns = new();

    internal EditorTestGlyphController(
        IWorkspaceService workspace,
        Func<string?, ITestExplorer> resolveExplorer,
        Func<string?, IReadOnlyList<EditorCoverageMarker>> coverageMarkersForPath,
        Dispatcher dispatcher,
        Action<string> showInfo,
        Action<string> showError)
    {
        _workspace = workspace;
        _resolveExplorer = resolveExplorer;
        _coverageMarkersForPath = coverageMarkersForPath;
        _dispatcher = dispatcher;
        _showInfo = showInfo;
        _showError = showError;
    }

    internal void ResetColumns() => _columns.Reset();

    internal void WireEditor(VimEditorControl control)
    {
        control.TestGlyphClicked += line0 => RunTestsAtLine(control, line0);
        control.BufferChanged += (_, _) => ScheduleSync(control);
        _editors.Add(new WeakReference<VimEditorControl>(control));
        SyncEditor(control);
    }

    internal void SyncAll()
    {
        for (var index = _editors.Count - 1; index >= 0; index--)
        {
            if (_editors[index].TryGetTarget(out var control))
            {
                if (!TrySync(control))
                    _editors.RemoveAt(index);
            }
            else
            {
                _editors.RemoveAt(index);
            }
        }
    }

    internal void SyncEditor(VimEditorControl control)
    {
        var path = control.FilePath;
        var explorer = _resolveExplorer(path);
        var glyphs = EditorTestGlyphMap.Build(_workspace, explorer.TestItems, path);
        var coverageMarkers = _coverageMarkersForPath(path);
        control.SetTestGlyphsEnabled(_columns.ShouldEnable(path, glyphs.Count));
        control.SetTestGlyphs(glyphs);
        control.SetCoverageMarkersEnabled(coverageMarkers.Count > 0);
        control.SetCoverageMarkers(coverageMarkers);
    }

    internal (ITestExplorer Explorer, TestItemViewModel Test)? TestAtCaret(VimEditorControl? control)
    {
        if (control is null) return null;
        var path = control.FilePath;
        var explorer = _resolveExplorer(path);
        return EditorTestGlyphMap.TestForCaret(_workspace, explorer.TestItems, path, control.Caret.Line) is { } test
            ? (explorer, test)
            : null;
    }

    internal void RunTestAtCaret(VimEditorControl? control)
    {
        if (TestAtCaret(control) is { } target)
            _ = RunTestsAsync(target.Explorer, [target.Test]);
        else
            _showInfo("カーソル行にテストがありません。");
    }

    private void ScheduleSync(VimEditorControl control)
    {
        if (!_syncPending.Add(control)) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _syncPending.Remove(control);
            TrySync(control);
        }));
    }

    private bool TrySync(VimEditorControl control)
    {
        try { SyncEditor(control); }
        catch (ObjectDisposedException) { return false; }
        catch (Exception exception)
        {
            // 生きているエディタの一過性の失敗。次の契機で回復できるよう一覧に残す。
            Trace.WriteLine($"[TestGlyphs] グリフの再送に失敗しました: {exception}");
        }
        return true;
    }

    private void RunTestsAtLine(VimEditorControl control, int line0)
    {
        var path = control.FilePath;
        var explorer = _resolveExplorer(path);
        _ = RunTestsAsync(explorer, EditorTestGlyphMap.TestsAt(_workspace, explorer.TestItems, path, line0));
    }

    private async Task RunTestsAsync(ITestExplorer explorer, IReadOnlyList<TestItemViewModel> tests)
    {
        if (tests.Count == 0)
        {
            _showInfo("カーソル行にテストがありません。");
            return;
        }
        foreach (var test in tests)
        {
            try
            {
                if (!await explorer.RunTestAsync(test))
                    _showInfo($"実行中のため、テストを開始できません: {test.DisplayName}");
            }
            catch (Exception exception)
            {
                _showError($"テストを実行できませんでした: {exception.Message}");
            }
        }
    }
}
