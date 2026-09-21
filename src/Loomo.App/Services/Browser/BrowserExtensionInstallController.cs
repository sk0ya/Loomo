using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>拡張機能の導入処理と、一覧UIの進行中・結果状態を同期する。</summary>
internal static class BrowserExtensionInstallController
{
    internal static async Task InstallFromStoreAsync(
        BrowserViewModel viewModel,
        BrowserExtensionStore store,
        string input,
        Func<Task<CoreWebView2Profile?>> ensureProfile,
        Func<BrowserExtensionRegistration, Task> completeInstall)
    {
        if (!BrowserExtensionStore.TryParseStoreId(input, out var storeId, out var kind))
        {
            viewModel.ExtensionStatus = "ストアの URL か、32 文字の拡張機能 ID を入れてください。";
            return;
        }
        if (await ensureProfile() is not { } profile)
        {
            viewModel.ExtensionStatus = "ブラウザタブを開いてから追加してください。";
            return;
        }

        viewModel.IsExtensionsBusy = true;
        viewModel.ExtensionStatus = "取得しています…";
        try
        {
            await completeInstall(await BrowserExtensionManager.DownloadAndInstallAsync(
                store, profile, storeId, kind));
        }
        catch (Exception ex)
        {
            viewModel.ExtensionStatus = $"追加できませんでした: {ex.Message}";
        }
        finally
        {
            viewModel.IsExtensionsBusy = false;
        }
    }

    internal static async Task InstallFromFolderAsync(
        BrowserViewModel viewModel,
        BrowserExtensionStore store,
        string folderPath,
        Func<Task<CoreWebView2Profile?>> ensureProfile,
        Func<BrowserExtensionRegistration, Task> completeInstall)
    {
        if (!BrowserExtensionStore.HasManifest(folderPath))
        {
            viewModel.ExtensionStatus = "そのフォルダーに manifest.json がありません。";
            return;
        }
        if (await ensureProfile() is not { } profile)
        {
            viewModel.ExtensionStatus = "ブラウザタブを開いてから追加してください。";
            return;
        }

        viewModel.IsExtensionsBusy = true;
        try
        {
            await completeInstall(await BrowserExtensionManager.AddFolderAsync(
                store, profile, folderPath, storeId: null, kind: null));
        }
        catch (Exception ex)
        {
            viewModel.ExtensionStatus = $"追加できませんでした: {ex.Message}";
        }
        finally
        {
            viewModel.IsExtensionsBusy = false;
        }
    }
}
