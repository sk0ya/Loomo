using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

internal sealed record BrowserExtensionRegistration(
    CoreWebView2BrowserExtension Extension,
    bool Remembered);

/// <summary>WebView2 プロファイルへの拡張機能登録と、出所記録の同期を行う。</summary>
internal static class BrowserExtensionManager
{
    public static string BrowserVersionNumber()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString().Split(' ')[0];
            return version.Length > 0 ? version : "120.0.0.0";
        }
        catch
        {
            return "120.0.0.0";
        }
    }

    public static async Task<BrowserExtensionRegistration> DownloadAndInstallAsync(
        BrowserExtensionStore store,
        CoreWebView2Profile profile,
        string id,
        BrowserExtensionStoreKind kind)
    {
        var extracted = await store.DownloadAsync(id, kind, BrowserVersionNumber());
        return await AddFolderAsync(store, profile, extracted.Directory, id, kind);
    }

    public static async Task<BrowserExtensionRegistration> AddFolderAsync(
        BrowserExtensionStore store,
        CoreWebView2Profile profile,
        string folderPath,
        string? storeId,
        BrowserExtensionStoreKind? kind)
    {
        var extension = await profile.AddBrowserExtensionAsync(folderPath);
        var remembered = store.Remember(new BrowserExtensionRecord
        {
            Id = extension.Id,
            FolderPath = folderPath,
            StoreId = storeId,
            StoreKind = kind?.ToString(),
            Name = extension.Name,
        });
        return new(extension, remembered);
    }

    public static async Task<CoreWebView2BrowserExtension?> FindAsync(
        CoreWebView2Profile profile,
        string id)
    {
        try
        {
            var installed = await profile.GetBrowserExtensionsAsync();
            return installed.FirstOrDefault(extension =>
                string.Equals(extension.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> SetEnabledAsync(
        CoreWebView2Profile profile,
        string id,
        bool enabled)
    {
        if (await FindAsync(profile, id) is not { } extension)
            return "切り替えられませんでした（拡張機能が見つかりません）。";
        try
        {
            await extension.EnableAsync(enabled);
            return null;
        }
        catch (Exception ex)
        {
            return $"切り替えられませんでした: {ex.Message}";
        }
    }

    public static async Task<bool> RemoveAsync(
        BrowserExtensionStore store,
        CoreWebView2Profile profile,
        string id,
        bool cleanFolders)
    {
        if (await FindAsync(profile, id) is not { } extension)
            return false;
        await extension.RemoveAsync();
        store.Forget(id, cleanFolders);
        return true;
    }
}

/// <summary>ページ単位で閉じた拡張機能の促しを記憶する。</summary>
internal sealed class BrowserExtensionPromptState
{
    private string? _dismissedStoreExtensionId;

    public bool ShouldShow(string storeExtensionId)
    {
        if (string.Equals(_dismissedStoreExtensionId, storeExtensionId, StringComparison.OrdinalIgnoreCase))
            return false;
        _dismissedStoreExtensionId = null;
        return true;
    }

    public void Dismiss(string? storeExtensionId)
        => _dismissedStoreExtensionId = storeExtensionId;
}
