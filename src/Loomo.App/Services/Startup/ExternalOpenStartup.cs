using System.Diagnostics;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>起動直後に「これは既存の部屋へ渡せば済む起動か」を決める（既定のアプリとしての振る舞い）。
///
/// <para><b>同時起動の取り合い。</b>エクスプローラーで5ファイルを選んで開くと、プロセスが5つ同時に起きる。
/// 部屋がまだ1つも無いと全員が「受け手なし」と判断して部屋が5つ立つので、受け手が無いときは
/// 起動用ミューテックスを取り合い、勝った1つだけが部屋を立てる。負けた側は勝者の受け口が立つ
/// （<see cref="ReleaseElection"/>）まで待ってから問い合わせ直し、勝者へ渡して終わる。</para></summary>
internal static class ExternalOpenStartup
{
    private const string ElectionMutexName = @"Local\sk0ya.Loomo.ExternalOpen.Startup";
    private static readonly TimeSpan ElectionWait = TimeSpan.FromSeconds(20);
    private static Mutex? _election;

    /// <summary>渡せるものを既存の部屋へ渡し、自分で開く残りを返す。<c>null</c> なら全部渡し終えた
    /// （このプロセスは何も開かずに終わってよい）。</summary>
    public static ExternalOpenRequest? Dispatch(ExternalOpenRequest request)
    {
        if (request.IsEmpty)
            return request;
        try
        {
            // ミューテックスはスレッドに紐づく。取るのも離す（ReleaseElection）のも UI スレッドで行い、
            // 非同期の問い合わせだけをプールで回して待つ（同期コンテキストへ戻らない。起動前なので待ってよい）。
            var instances = Run(InstanceRelayClient.ProbeAsync);
            if (instances.Count == 0 && NeedsReceiver(request) && !TryWinElection())
            {
                // 誰かが部屋を立てている最中。受け口が立つのを待ってから問い合わせ直す。
                WaitForWinner();
                instances = Run(InstanceRelayClient.ProbeAsync);
            }

            ExternalOpenRequest? leftover = null;
            foreach (var (processId, part) in ExternalOpenRouting.Route(request, instances))
            {
                if (processId is { } pid && Run(() => InstanceRelayClient.OpenAsync(pid, part)))
                    continue;
                leftover = leftover is null ? part : Merge(leftover, part);
            }
            return leftover;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[中継] 振り分けに失敗。自分で開く: {ex}");
            return request;
        }
    }

    private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>自分の受け口が立った（＝後から来た起動が自分へ渡せるようになった）ので、取り合いを降りる。</summary>
    public static void ReleaseElection()
    {
        var election = Interlocked.Exchange(ref _election, null);
        if (election is null)
            return;
        try { election.ReleaseMutex(); }
        catch (ApplicationException) { /* 別スレッドで取った等。破棄だけする */ }
        election.Dispose();
    }

    /// <summary>フォルダーだけの要求は、受け手が無ければ新しい部屋で開くのが正しい動き——取り合わない。</summary>
    private static bool NeedsReceiver(ExternalOpenRequest request) => request.Files.Count > 0 || request.Urls.Count > 0;

    private static bool TryWinElection()
    {
        var mutex = new Mutex(false, ElectionMutexName);
        try
        {
            if (mutex.WaitOne(0))
            {
                _election = mutex;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _election = mutex;
            return true;
        }
        mutex.Dispose();
        return false;
    }

    private static void WaitForWinner()
    {
        using var mutex = new Mutex(false, ElectionMutexName);
        try
        {
            if (mutex.WaitOne(ElectionWait))
                mutex.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
            mutex.ReleaseMutex();
        }
    }

    private static ExternalOpenRequest Merge(ExternalOpenRequest a, ExternalOpenRequest b)
        => new(a.WorkspaceFolder ?? b.WorkspaceFolder, [.. a.Files, .. b.Files], [.. a.Urls, .. b.Urls]);
}
