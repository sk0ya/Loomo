namespace sk0ya.Loomo.App.Services;

/// <summary>再ペアレントの Unloaded/Loaded 対から、作り直しが必要か判断する。</summary>
internal sealed class ReparentRebuildPolicy
{
    private bool _reattachPending;

    public void OnUnloaded() => _reattachPending = true;

    /// <summary>初回Loadedでは false、Unloaded後のLoadedで一度だけ true を返す。</summary>
    public bool ConsumeLoaded()
    {
        if (!_reattachPending)
            return false;
        _reattachPending = false;
        return true;
    }
}
