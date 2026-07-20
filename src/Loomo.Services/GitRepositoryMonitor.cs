using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>Git UI の表示中だけリポジトリ状態をポーリングし、変化を通知する。</summary>
public sealed class GitRepositoryMonitor
{
    private const int PollIntervalMs = 1500;
    private readonly GitRootState _rootState;
    private readonly GitCommandRunner _runner;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private Timer? _pollTimer;
    private int _liveTrackers;
    private string? _lastSignature;

    public GitRepositoryMonitor(GitRootState rootState, GitCommandRunner runner)
    {
        _rootState = rootState;
        _runner = runner;
        rootState.Changed += (_, _) =>
        {
            _lastSignature = null;
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? RepositoryChanged;

    public IDisposable TrackLiveChanges()
    {
        if (Interlocked.Increment(ref _liveTrackers) == 1)
        {
            _lastSignature = null;
            _pollTimer ??= new Timer(_ => _ = PollOnceAsync());
            _pollTimer.Change(PollIntervalMs, PollIntervalMs);
        }
        return new LiveTracker(this);
    }

    private void ReleaseLiveTracking()
    {
        if (Interlocked.Decrement(ref _liveTrackers) == 0)
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task PollOnceAsync()
    {
        if (!_pollGate.Wait(0)) return;
        try
        {
            var root = _rootState.CurrentRoot;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var result = await _runner.RunAsync(
                "--no-optional-locks", "status", "--porcelain=v2", "--branch").ConfigureAwait(false);
            if (!result.Success) return;
            var previous = _lastSignature;
            _lastSignature = result.Output;
            if (previous is not null
                && !string.Equals(previous, result.Output, StringComparison.Ordinal))
                RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private sealed class LiveTracker : IDisposable
    {
        private GitRepositoryMonitor? _owner;
        public LiveTracker(GitRepositoryMonitor owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLiveTracking();
    }
}
