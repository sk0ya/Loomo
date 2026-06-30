using System;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペース内のローカルファイルを WebView2 の仮想 HTTPS オリジンへ変換する。</summary>
internal static class BrowserLocalContent
{
    public const string VirtualHost = "workspace.loomo";

    /// <summary>
    /// <paramref name="url"/> がワークスペース配下の file URL なら、同じ相対位置の仮想 HTTPS URL を返す。
    /// 通常 URL、無効なパス、ワークスペース外のファイルは変更しない。
    /// </summary>
    public static string MapFileUrl(string url, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.IsFile)
        {
            return url;
        }

        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var file = Path.GetFullPath(uri.LocalPath);
            var relative = Path.GetRelativePath(root, file);
            if (relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return url;
            }

            var encodedPath = string.Join(
                '/',
                relative.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));

            return $"https://{VirtualHost}/{encodedPath}{uri.Query}{uri.Fragment}";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return url;
        }
    }
}
