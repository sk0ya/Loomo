using System;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>file:// ページに許可するローカルファイルを、現在のワークスペース配下へ制限する。</summary>
internal static class BrowserFileAccess
{
    private const string FileUrlFilter = "file://*";

    /// <summary>WebView2 の全 file:// 要求を監視し、ワークスペース外なら 403 で遮断する。</summary>
    public static void RestrictToWorkspace(CoreWebView2 core, Func<string?> workspaceRootProvider)
    {
        core.AddWebResourceRequestedFilter(
            FileUrlFilter,
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.Document);

        core.WebResourceRequested += (_, e) =>
        {
            if (IsAllowed(e.Request.Uri, workspaceRootProvider()))
                return;

            e.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null,
                403,
                "Forbidden",
                "Content-Type: text/plain\r\nCache-Control: no-store");
        };
    }

    /// <summary>file URL がワークスペースルート自身またはその配下を指す場合だけ許可する。</summary>
    internal static bool IsAllowed(string requestUri, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
            || !uri.IsFile)
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var requestedPath = Path.GetFullPath(uri.LocalPath);
            var relative = Path.GetRelativePath(root, requestedPath);
            return relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                   && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
