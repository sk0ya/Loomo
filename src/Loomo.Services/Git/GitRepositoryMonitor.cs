using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>Git UI の表示中だけリポジトリ状態をポーリングし、変化を通知する。</summary>
public sealed class GitRepositoryMonitor
{
    private const int PollIntervalMs = 1500;

    /// <summary>更新時刻を見に行く変更ファイル数の上限。巨大なチェックアウト直後などに
    /// 1.5秒ごとに数千回 stat しないための頭打ち（そこまで変更が多ければ一覧の方が先に動く）。</summary>
    private const int MaxStampedFiles = 1000;
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
            var signature = result.Output + BuildWorkingTreeStamp(root, result.Output);
            var previous = _lastSignature;
            _lastSignature = signature;
            if (previous is not null
                && !string.Equals(previous, signature, StringComparison.Ordinal))
                RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// 作業ツリーの<b>内容だけ</b>の変化を署名に混ぜる。<c>git status --porcelain=v2</c> の行が持つのは
    /// HEAD と index の object id だけで、作業ツリーの内容は入らない——すでに <c>1 .M</c> と出ている
    /// ファイルがもう一度書き換わっても出力は1バイトも変わらず、ポーリングは「無変化」と判断してしまう。
    /// 見ている差分がその編集の前のまま固まるので（Diff ペインは <see cref="RepositoryChanged"/> でしか
    /// 読み直さない）、変更ファイルのサイズと更新時刻を足して見分ける。
    /// 読めないもの（消えた・権限が無い・引用符付きのパス）は印を付けない＝無変化として扱う。
    /// </summary>
    internal static string BuildWorkingTreeStamp(string root, string statusOutput)
    {
        var snapshot = GitStatusParser.Parse(statusOutput);
        var builder = new StringBuilder();
        var stamped = 0;
        foreach (var path in Paths(snapshot))
        {
            if (stamped++ >= MaxStampedFiles) break;
            builder.Append('\n').Append(path).Append('|');
            try
            {
                var info = new FileInfo(Path.Combine(root, path));
                if (info.Exists)
                    builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
            }
        }
        return builder.ToString();
    }

    /// <summary>変更ファイルのパス（重複を除く）。ステージ済みも見るのは、同じファイルが
    /// ステージと作業ツリーの両方に出ていても印は1つでよいから。</summary>
    private static IEnumerable<string> Paths(GitStatusSnapshot snapshot)
        => snapshot.Unstaged.Concat(snapshot.Staged)
            .Select(entry => entry.Path)
            .Distinct(StringComparer.Ordinal);

    private sealed class LiveTracker : IDisposable
    {
        private GitRepositoryMonitor? _owner;
        public LiveTracker(GitRepositoryMonitor owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLiveTracking();
    }
}
