using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <see cref="LocalFileUrl"/>（<c>https://file.loomo/C:/…</c>）の要求を、ディスクのファイルで直接答える。
/// フォルダーの割り当て（<c>SetVirtualHostNameToFolderMapping</c>）もコピーも使わない。
/// <see cref="Views.LoomoWebView2"/> が CoreWebView2 を作った時点で付けるので、アプリ内のどの WebView2
/// （ブラウザペイン・EditorSupport・切り離し窓）でも同じ URL が開ける。何に答えるかは <see cref="LocalFileAccess"/>。
/// </summary>
internal static class LocalFileServer
{
    /// <summary>WebView2 ごとの「いま表に出ているページ」。</summary>
    private sealed class PageState
    {
        /// <summary>始まったばかりのトップレベル遷移の行き先（その文書要求だけは範囲を問わず通す）。</summary>
        public string? PendingTopLevelPath;
        /// <summary>表のページが部品として読める範囲。ネットのページなら null（＝何も読ませない）。</summary>
        public string? Scope;
    }

    private static readonly ConditionalWeakTable<CoreWebView2, PageState> States = new();
    private static Func<string, string?>? _workspaceFolderFor;

    /// <summary>範囲の基準にするワークスペースを渡す（起動時に1回）。無ければページのフォルダーが範囲になる。</summary>
    public static void UseWorkspace(IWorkspaceService workspace)
        => _workspaceFolderFor = workspace.FolderFor;

    public static void Attach(CoreWebView2 core)
    {
        try
        {
            States.AddOrUpdate(core, new PageState());
            // Worker（Service/Shared）からの要求も拾う。既定の重載は文書からの要求しか回さず、
            // 拾えなかった要求は本物のネットワークへ出て名前解決で失敗する。
            core.AddWebResourceRequestedFilter(LocalFileUrl.Origin + "/*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
        }
        catch { /* 付けられなくても、ほかの表示はそのまま動く */ }
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (sender is not CoreWebView2 core || !States.TryGetValue(core, out var state))
            return;
        if (!LocalFileUrl.TryToPath(e.Uri, out var path))
        {
            // ネットのページ（など）へ移る：部品としてのローカル読み出しは一切通さない。
            state.PendingTopLevelPath = null;
            state.Scope = null;
            return;
        }
        if (!LocalFileAccess.AllowsTopLevelNavigation(state.Scope is not null, e.IsUserInitiated))
        {
            e.Cancel = true;
            return;
        }
        state.PendingTopLevelPath = path;
        state.Scope = LocalFileAccess.ScopeOf(path, _workspaceFolderFor);
    }

    private static async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core || !LocalFileUrl.IsLocalFileUrl(e.Request.Uri))
            return;

        CoreWebView2Deferral? deferral = null;
        try
        {
            // 答えられない URL にも必ず答える——黙って返すと本物のネットワークへ出て file.loomo の名前解決が走る
            // （自動の /favicon.ico や、ルート相対の /x.js が毎回それになる）。
            LocalFileResponse response;
            if (!LocalFileUrl.TryToPath(e.Request.Uri, out var path))
            {
                response = LocalFileResponse.NotFound();
            }
            else if (!IsAllowed(core, e.ResourceContext, path))
            {
                response = LocalFileResponse.Forbidden();
            }
            else
            {
                var range = Header(e.Request.Headers, "Range");
                var uri = e.Request.Uri;
                deferral = e.GetDeferral();
                // 開く・読むはディスク待ち（ネットワークドライブなら尚更）なので UI スレッドから外す。
                response = await Task.Run(() => LocalFileResponse.Create(path, range, uri));
            }
            e.Response = core.Environment.CreateWebResourceResponse(
                response.Content, response.Status, response.Reason, response.Headers);
        }
        catch { /* 応答を作れなければ、ふつうの読み込み失敗として見える */ }
        finally
        {
            try { deferral?.Complete(); } catch { }
        }
    }

    private static bool IsAllowed(CoreWebView2 core, CoreWebView2WebResourceContext context, string path)
    {
        if (!States.TryGetValue(core, out var state))
            return false;
        if (context == CoreWebView2WebResourceContext.Document
            && state.PendingTopLevelPath is { } pending
            && string.Equals(pending, path, StringComparison.OrdinalIgnoreCase))
        {
            state.PendingTopLevelPath = null;
            return true;
        }
        return LocalFileAccess.IsWithin(state.Scope, path);
    }

    private static string? Header(CoreWebView2HttpRequestHeaders headers, string name)
    {
        try { return headers.Contains(name) ? headers.GetHeader(name) : null; }
        catch { return null; }
    }
}
