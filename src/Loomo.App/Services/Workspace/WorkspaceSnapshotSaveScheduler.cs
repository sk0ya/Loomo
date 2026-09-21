using System.Windows.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペーススナップショット保存をまとめ、終了・切替時は保留分を同期保存へ切り替える。</summary>
internal static class WorkspaceSnapshotSaveScheduler
{
    public static DispatcherOperation? Schedule(
        Dispatcher dispatcher,
        DispatcherOperation? pending,
        bool immediate,
        Action<bool> saveSnapshot,
        Action clearPending)
    {
        if (immediate)
        {
            pending?.Abort();
            clearPending();
            saveSnapshot(true);
            return null;
        }

        if (pending is { Status: DispatcherOperationStatus.Pending })
            return pending;

        return dispatcher.BeginInvoke(new Action(() =>
        {
            clearPending();
            saveSnapshot(false);
        }), DispatcherPriority.ApplicationIdle);
    }
}
