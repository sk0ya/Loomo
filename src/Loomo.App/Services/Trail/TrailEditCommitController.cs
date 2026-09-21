using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタの編集記録を一定時間まとめ、最後のカーソル位置を軌跡へ残す。</summary>
internal sealed class TrailEditCommitController(
    TrailViewModel trail,
    Func<bool> isSuppressed,
    Action<Action<DisplayMode, PaneKind?, string?>> record)
{
    private DispatcherTimer? _timer;
    private EditorTab? _pendingTab;

    public void Request(EditorTab tab)
    {
        if (isSuppressed() || !tab.IsRealized || !tab.Control.IsModified
            || !TrailLogic.IsRecordableFile(tab.PeekFilePath, tab.PeekIsVirtual))
            return;

        if (_pendingTab is { } pending && !ReferenceEquals(pending, tab))
            CommitPending();

        _pendingTab = tab;
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) => CommitPending();
        return timer;
    }

    private void CommitPending()
    {
        _timer?.Stop();
        if (_pendingTab is not { } tab)
            return;
        _pendingTab = null;
        if (isSuppressed() || !tab.IsRealized || !tab.Control.IsModified)
            return;

        var path = tab.PeekFilePath;
        if (!TrailLogic.IsRecordableFile(path, tab.PeekIsVirtual))
            return;
        var line = tab.Control.Caret.Line;
        var column = tab.Control.Caret.Column;
        record((mode, stagePane, layout) =>
            trail.RecordEdit(path, line, column, mode, stagePane, layout));
    }
}
