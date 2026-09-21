using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>旧テスト契約をサービス側の進捗状態へ委譲する。</summary>
internal sealed class ChunkedDocumentBuild(int rowCount, Action<int, int> append)
{
    private readonly ChunkedAppendState _state = new(rowCount, append);
    internal bool IsRunning => _state.IsRunning;
    internal bool Cancelled => _state.Cancelled;
    internal void Cancel() => _state.Cancel();
    internal void Step(int chunk) => _state.Step(chunk);
    internal void Finish() => _state.Finish();
}

internal enum EditorSupportPageAction { None, ReloadCurrentPage, RequestReload }
internal enum EditorSupportPageStatus { Idle, Loading, Ready, Failed }
internal sealed record EditorSupportPageId(string? Uri, string? PageKey, bool CanReload = true);

/// <summary>ページ状態の既存View契約をサービス側の状態機械へ委譲する。</summary>
internal sealed class EditorSupportPageState
{
    private readonly EditorSupportPageStateMachine _state = new();
    public EditorSupportPageStatus Status => _state.Status switch
    {
        EditorSupportPageLoadStatus.Loading => EditorSupportPageStatus.Loading,
        EditorSupportPageLoadStatus.Ready => EditorSupportPageStatus.Ready,
        EditorSupportPageLoadStatus.Failed => EditorSupportPageStatus.Failed,
        _ => EditorSupportPageStatus.Idle,
    };
    public string? CurrentUri => _state.CurrentUri;
    public string? ReadyPageKey => _state.ReadyPageKey;
    public bool IsShowing(string uri) => _state.IsShowing(uri);
    public bool CanPatchBody(string? pageKey) => _state.CanPatchBody(pageKey);
    public void BeginLoad(EditorSupportPageId id)
        => _state.BeginLoad(new EditorSupportPageLoadId(id.Uri, id.PageKey, id.CanReload));
    public void Fail() => _state.Fail();
    public EditorSupportPageAction Completed(bool success) => Map(_state.Completed(success));
    public bool BeginCurrentPageReload() => _state.BeginCurrentPageReload();
    public EditorSupportPageAction WatchdogFired() => Map(_state.WatchdogFired());
    public void Reset() => _state.Reset();
    public void ResetFirstRenderHealing() => _state.ResetFirstRenderHealing();
    private static EditorSupportPageAction Map(EditorSupportPageLoadAction action) => action switch
    {
        EditorSupportPageLoadAction.ReloadCurrentPage => EditorSupportPageAction.ReloadCurrentPage,
        EditorSupportPageLoadAction.RequestReload => EditorSupportPageAction.RequestReload,
        _ => EditorSupportPageAction.None,
    };
}

/// <summary>描画エンジンの既存テスト名を保つ薄い委譲口。</summary>
internal sealed class EditorSupportRenderFlow
{
    internal const int ConnectingNoticeGraceTicks = EditorSupportRenderEngine.ConnectingNoticeGraceTicks;
    private readonly EditorSupportRenderEngine _engine;

    public EditorSupportRenderFlow(
        EditorSupportResolver resolver,
        EditorSupportController state,
        IWorkspaceService workspace,
        ILspWorkspace lspWorkspace,
        CodeEditorSupport codeSupport,
        IEditorSupportRenderHost host)
        => _engine = new EditorSupportRenderEngine(resolver, state, workspace, lspWorkspace, codeSupport, host);

    public Task RenderAsync(
        EditorSupportRenderRequest request,
        EditorSupportUpdateReason reason,
        Func<EditorSupportFrame, bool> apply,
        CancellationToken cancellationToken)
        => _engine.RenderAsync(request, reason, apply, cancellationToken);
}
