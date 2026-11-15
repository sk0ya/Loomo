using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Motion;

namespace sk0ya.Loomo.Services.Motion;

/// <summary>
/// 動きを録る窓口（§44.3）。録るのは<b>同時に 1 つ</b>：ターンの終わりが録っている最中に来たら、終わってから 1 回だけ
/// 録り直す（いちばん新しい頼みごとで）。見どころの行を人間が許していなければ何も回さない。
/// ターンの終わりに回すのは <c>when: "turn"</c> の見どころだけで、前に録ってから作業ツリーが変わっていなければ録らない。
/// </summary>
public sealed class MotionService(MotionStore store)
{
    private readonly MotionRecorder _recorder = new(store);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingGate = new();
    private (string Folder, MotionOrigin Origin)? _pendingTurn;
    private bool _turnLoopRunning;

    public MotionStore Store => store;

    /// <summary>録った（背景スレッドで鳴る）。引数はワークスペースの主フォルダー。</summary>
    public event Action<string, MotionSnapshot>? Recorded;

    /// <summary>いま録っているか。</summary>
    public bool IsRecording => _gate.CurrentCount == 0;

    /// <summary>許された見どころ（許されていなければ空）。</summary>
    public IReadOnlyList<MotionProbe> ApprovedProbes(string workspaceFolder)
    {
        var probes = store.LoadProbes(workspaceFolder, out var error);
        return error is null && probes.Count > 0 && store.IsApproved(workspaceFolder, probes) ? probes : [];
    }

    /// <summary>ターンが終わった。録るのは裏で、呼び手は待たない。</summary>
    public void RequestTurnRecord(string workspaceFolder, MotionOrigin origin)
    {
        lock (_pendingGate)
        {
            _pendingTurn = (workspaceFolder, origin);
            if (_turnLoopRunning)
                return;
            _turnLoopRunning = true;
        }
        _ = Task.Run(TurnLoopAsync);
    }

    private async Task TurnLoopAsync()
    {
        while (true)
        {
            (string Folder, MotionOrigin Origin) next;
            lock (_pendingGate)
            {
                if (_pendingTurn is not { } pending)
                {
                    _turnLoopRunning = false;
                    return;
                }
                next = pending;
                _pendingTurn = null;
            }
            try
            {
                var probes = ApprovedProbes(next.Folder).Where(probe => probe.EveryTurn).ToArray();
                if (probes.Length > 0)
                    await RecordAsync(next.Folder, probes, next.Origin, CancellationToken.None,
                        skipIfStamp: store.Latest(next.Folder)?.Stamp).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine($"動きを録れませんでした: {ex.Message}");
            }
        }
    }

    /// <summary>許された見どころを全部回して録る（手で・言いつけから）。</summary>
    public Task<MotionSnapshot?> RecordAllAsync(string workspaceFolder, MotionOrigin origin, CancellationToken cancel,
        string? runFolder = null, string? orderResultId = null)
        => RecordAsync(workspaceFolder, ApprovedProbes(workspaceFolder), origin, cancel, runFolder, null, orderResultId);

    private async Task<MotionSnapshot?> RecordAsync(string workspaceFolder, IReadOnlyList<MotionProbe> probes, MotionOrigin origin,
        CancellationToken cancel, string? runFolder = null, string? skipIfStamp = null, string? orderResultId = null)
    {
        if (probes.Count == 0)
            return null;
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        MotionSnapshot? snapshot;
        try
        {
            snapshot = await _recorder.RecordAsync(workspaceFolder, probes, origin, cancel, runFolder, skipIfStamp, orderResultId)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (snapshot is not null)
            Recorded?.Invoke(workspaceFolder, snapshot);
        return snapshot;
    }
}
