using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 と保存した manifest 情報を、拡張機能一覧の表示モデルへ変換する。</summary>
internal static class BrowserExtensionDisplayMapper
{
    internal static BrowserExtensionInstallPresentation Install(BrowserExtensionRegistration registration)
        => new(
            $"「{registration.Extension.Name}」を追加しました。開いているページは再読み込みしてください。",
            registration.Remembered
                ? null
                : "出所を記録できませんでした（browser-extensions.json）。ボタンや設定画面は出ません。");

    internal static BrowserExtensionViewModel Map(
        CoreWebView2BrowserExtension extension,
        IReadOnlyList<BrowserExtensionRecord> records)
    {
        var record = records.FirstOrDefault(r =>
            string.Equals(r.Id, extension.Id, StringComparison.OrdinalIgnoreCase));
        var manifest = record?.FolderPath is { Length: > 0 } folder
            ? BrowserExtensionStore.ReadManifest(folder)
            : null;

        return new BrowserExtensionViewModel(extension.IsEnabled)
        {
            Id = extension.Id,
            // WebView2 の名前は __MSG_…__ を解決済み。
            Name = string.IsNullOrWhiteSpace(extension.Name) ? extension.Id : extension.Name,
            Version = manifest?.Version,
            FolderPath = record?.FolderPath,
            IconPath = ResolveAsset(record?.FolderPath, manifest?.IconPath),
            PopupUrl = ExtensionPageUrl(extension.Id, manifest?.PopupPath),
            OptionsUrl = ExtensionPageUrl(extension.Id, manifest?.OptionsPath),
        };
    }

    /// <summary>ストアのタイトルから、ページ名部分だけを取り出す。</summary>
    internal static string StoreName(string? title)
    {
        var cut = (title ?? "").Split([" - ", " – ", " | "], StringSplitOptions.None)[0].Trim();
        return cut.Length > 0 ? cut : "この拡張機能";
    }

    private static string? ExtensionPageUrl(string id, string? path)
        => string.IsNullOrWhiteSpace(path) ? null : $"chrome-extension://{id}/{path.TrimStart('/')}";

    private static string? ResolveAsset(string? folderPath, string? relativePath)
    {
        if (folderPath is not { Length: > 0 } || relativePath is not { Length: > 0 })
            return null;
        var full = Path.Combine(folderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }
}

internal sealed record BrowserExtensionInstallPresentation(string SuccessMessage, string? StatusWarning);
