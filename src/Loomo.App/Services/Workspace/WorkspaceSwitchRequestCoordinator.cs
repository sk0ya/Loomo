namespace sk0ya.Loomo.App.Services;

/// <summary>競合するワークスペース切替要求を最新の1件へ畳み、切替処理を1本に保つ。</summary>
internal sealed class WorkspaceSwitchRequestCoordinator
{
    private readonly object _gate = new();
    private WorkspaceSwitchRequest? _pending;
    private bool _running;

    /// <summary>切替ループを新たに開始する必要がある場合だけ true を返す。</summary>
    public bool Queue(WorkspaceSnapshot workspace, bool captureCurrent)
    {
        lock (_gate)
        {
            _pending = new(workspace, captureCurrent);
            if (_running)
                return false;
            _running = true;
            return true;
        }
    }

    /// <summary>UIが指定する初期待機のあと、保留要求がなくなるまで順に切り替える。</summary>
    public async Task DrainAsync(Func<Task> yieldBeforeSwitch,
        Func<WorkspaceSwitchRequest, Task> switchWorkspace, Action<Exception> reportFailure)
    {
        await yieldBeforeSwitch();

        while (true)
        {
            WorkspaceSwitchRequest request;
            lock (_gate)
            {
                if (_pending is not { } pending)
                {
                    _running = false;
                    return;
                }
                request = pending;
                _pending = null;
            }

            try
            {
                await switchWorkspace(request);
            }
            catch (Exception ex)
            {
                reportFailure(ex);
            }
        }
    }
}

internal readonly record struct WorkspaceSwitchRequest(
    WorkspaceSnapshot Workspace, bool CaptureCurrent);
