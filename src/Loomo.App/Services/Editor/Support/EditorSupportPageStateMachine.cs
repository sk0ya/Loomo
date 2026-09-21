namespace sk0ya.Loomo.App.Services;

/// <summary>WebView ページの読み込み・失敗・再試行状態を管理する UI 非依存の状態機械。</summary>
internal enum EditorSupportPageLoadStatus { Idle, Loading, Ready, Failed }
internal enum EditorSupportPageLoadAction { None, ReloadCurrentPage, RequestReload }
internal sealed record EditorSupportPageLoadId(string? Uri, string? PageKey, bool CanReload);

/// <summary>WebView ページの適用結果。本文差し替えができないときはページ全体の再構築を要求する。</summary>
internal enum EditorSupportPageApplyResult { Applied, NeedsFullPage }

internal sealed class EditorSupportPageStateMachine
{
    private EditorSupportPageLoadId? _pageId;
    private EditorSupportPageLoadStatus _status = EditorSupportPageLoadStatus.Idle;
    private EditorSupportPageLoadId? _lastFailedId;
    private bool _firstRenderHealed;

    public EditorSupportPageLoadStatus Status => _status;
    public string? CurrentUri => _pageId?.Uri;
    public string? ReadyPageKey => _status == EditorSupportPageLoadStatus.Ready ? _pageId?.PageKey : null;

    public bool IsShowing(string uri)
        => _status == EditorSupportPageLoadStatus.Ready
           && string.Equals(_pageId?.Uri, uri, StringComparison.OrdinalIgnoreCase);

    public bool CanPatchBody(string? pageKey)
        => _status == EditorSupportPageLoadStatus.Ready
           && _pageId is { PageKey: { } current }
           && pageKey is not null
           && current == pageKey;

    public void BeginLoad(EditorSupportPageLoadId id)
    {
        if (_lastFailedId is not null && _lastFailedId != id)
            _lastFailedId = null;
        _pageId = id;
        _status = EditorSupportPageLoadStatus.Loading;
    }

    public void Fail()
    {
        _pageId = null;
        _status = EditorSupportPageLoadStatus.Failed;
    }

    public EditorSupportPageLoadAction Completed(bool success)
    {
        var attempted = _pageId;
        if (success)
        {
            _status = EditorSupportPageLoadStatus.Ready;
            _lastFailedId = null;
        }
        else
        {
            Fail();
        }

        if (!_firstRenderHealed)
        {
            _firstRenderHealed = true;
            return success && attempted?.CanReload == true
                ? EditorSupportPageLoadAction.ReloadCurrentPage
                : EditorSupportPageLoadAction.RequestReload;
        }

        return !success && ShouldRetryAfterFailure(attempted)
            ? EditorSupportPageLoadAction.RequestReload
            : EditorSupportPageLoadAction.None;
    }

    public bool BeginCurrentPageReload()
    {
        if (_status != EditorSupportPageLoadStatus.Ready || _pageId is null)
            return false;
        _status = EditorSupportPageLoadStatus.Loading;
        return true;
    }

    public EditorSupportPageLoadAction WatchdogFired()
    {
        if (_status != EditorSupportPageLoadStatus.Loading)
            return EditorSupportPageLoadAction.None;
        var attempted = _pageId;
        Fail();
        return ShouldRetryAfterFailure(attempted)
            ? EditorSupportPageLoadAction.RequestReload
            : EditorSupportPageLoadAction.None;
    }

    public void Reset()
    {
        _pageId = null;
        _lastFailedId = null;
        _status = EditorSupportPageLoadStatus.Idle;
    }

    public void ResetFirstRenderHealing() => _firstRenderHealed = false;

    private bool ShouldRetryAfterFailure(EditorSupportPageLoadId? failed)
    {
        if (_lastFailedId == failed)
            return false;
        _lastFailedId = failed;
        return true;
    }
}
