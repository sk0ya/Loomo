using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal sealed record BrowserExtensionListResult(
    IReadOnlyList<BrowserExtensionViewModel>? Items,
    string StatusText);

/// <summary>WebView2 の登録一覧と Loomo の出所記録を拡張機能表示モデルへまとめる。</summary>
internal static class BrowserExtensionListService
{
    internal static async Task<BrowserExtensionListResult> LoadAsync(
        CoreWebView2Profile profile, BrowserExtensionStore store, bool isInstalling)
    {
        try
        {
            var installed = await profile.GetBrowserExtensionsAsync();
            // 登録の途中は展開中フォルダーを孤児と誤認するので、その間は掃除しない。
            if (!isInstalling)
                store.CleanOrphanFolders();
            var records = store.LoadRecords();
            var items = installed
                .Select(extension => BrowserExtensionDisplayMapper.Map(extension, records))
                .ToArray();
            var status = installed.Count == 0
                ? "拡張機能はまだありません。ストアの URL か ID、または展開済みフォルダーから追加できます。"
                : "";
            return new(items, status);
        }
        catch (Exception ex)
        {
            return new(null, $"一覧を取得できませんでした: {ex.Message}");
        }
    }
}
