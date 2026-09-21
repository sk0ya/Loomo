using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>軌跡ジャンプを直列化し、連続する要求を最新の1件へまとめる。</summary>
internal sealed class TrailJumpCoordinator(
    TrailViewModel trail,
    Func<TrailEntryViewModel, bool> canJump,
    Action<TrailEntryViewModel> restoreDisplayContext,
    Func<bool> getSuppressed,
    Action<bool> setSuppressed,
    Action<bool> setBrowsingPast)
{
    private readonly Dictionary<TrailEntryKind, Func<TrailEntryViewModel, Task>> _jumps = new();
    private TrailEntryViewModel? _pendingEntry;
    private bool _running;
    private bool _baseSuppressed;
    private DispatcherTimer? _settleTimer;

    public void Register(TrailEntryKind kind, Func<TrailEntryViewModel, Task> jump)
        => _jumps[kind] = jump;

    public void Request(TrailEntryViewModel entry)
    {
        _pendingEntry = entry;
        if (!_running)
            ProcessAsync();
    }

    private async void ProcessAsync()
    {
        if (_running)
            return;
        _running = true;
        if (_settleTimer is not { IsEnabled: true })
            _baseSuppressed = getSuppressed();
        _settleTimer?.Stop();
        setSuppressed(true);
        try
        {
            while (_pendingEntry is { } entry)
            {
                _pendingEntry = null;
                if (!_jumps.TryGetValue(entry.Kind, out var jump) || !canJump(entry))
                    continue;
                restoreDisplayContext(entry);
                await jump(entry);
            }
        }
        finally
        {
            _running = false;
        }

        setBrowsingPast(trail.CurrentIndex < trail.Entries.Count - 1);
        _settleTimer ??= CreateSettleTimer();
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private DispatcherTimer CreateSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_running)
                setSuppressed(_baseSuppressed);
        };
        return timer;
    }
}
