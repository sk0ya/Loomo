using System.Windows.Threading;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Services;

/// <summary>短時間に続くペイン移動をまとめ、最後にフォーカスされた位置だけを記録する。</summary>
internal sealed class TrailPaneCommitController(
    Func<bool> isSuppressed,
    Func<bool> isStageActive,
    Func<DisplayMode> currentMode,
    Func<PaneKind?> focusedPane,
    Action<PaneKind> commit)
{
    private PaneKind? _lastPane;
    private DisplayMode? _lastMode;
    private PaneKind? _pendingPane;
    private DispatcherTimer? _timer;

    public void Request(PaneKind pane)
    {
        if (isSuppressed() || isStageActive())
            return;
        var mode = currentMode();
        if (_lastPane == pane && _lastMode == mode)
        {
            _pendingPane = null;
            _timer?.Stop();
            return;
        }

        _pendingPane = pane;
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    public void Remember(PaneKind pane, DisplayMode mode)
    {
        _lastPane = pane;
        _lastMode = mode;
    }

    public void Reset()
    {
        _lastPane = null;
        _lastMode = null;
        _pendingPane = null;
        _timer?.Stop();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_pendingPane is not { } pane)
                return;
            _pendingPane = null;
            var mode = currentMode();
            if (isSuppressed() || isStageActive()
                || (_lastPane == pane && _lastMode == mode)
                || focusedPane() != pane)
                return;

            Remember(pane, mode);
            commit(pane);
        };
        return timer;
    }
}
