namespace sk0ya.Loomo.App.Services;

internal readonly record struct BrowserExtensionPromptDecision(
    bool IsStorePage, string? StoreId, bool ShouldPrompt);

/// <summary>表示中URLと閉じたページ状態から拡張機能の促し状態を決める。</summary>
internal static class BrowserExtensionPromptPolicy
{
    internal static BrowserExtensionPromptDecision Evaluate(
        BrowserExtensionPromptState state,
        string? url)
    {
        if (!BrowserExtensionStore.TryParseStoreDetail(url, out var storeId, out _))
            return default;
        return new(true, storeId, state.ShouldShow(storeId));
    }

    internal static bool IsInstalled(
        string storeId, IEnumerable<BrowserExtensionRecord> records)
        => records.Any(record =>
            string.Equals(record.StoreId, storeId, StringComparison.OrdinalIgnoreCase));

    internal static void Dismiss(BrowserExtensionPromptState state, string? url)
    {
        var storeId = BrowserExtensionStore.TryParseStoreDetail(url, out var id, out _) ? id : null;
        state.Dismiss(storeId);
    }
}
