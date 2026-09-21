namespace sk0ya.Loomo.App.Services;

/// <summary>ページから届く WebMessage のうち、ストアの追加操作として受け入れるものを判定する。</summary>
internal static class BrowserExtensionMessagePolicy
{
    internal static bool IsStoreInstallRequest(string? message, string? source)
    {
        if (!BrowserExtensionStore.TryParseStoreDetail(source, out _, out _))
            return false;
        try
        {
            var outer = JsonSerializer.Deserialize<JsonElement>(message ?? "");
            // postMessage(string) は JSON 文字列として届くため、必要ならもう一度解く。
            var payload = outer.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(outer.GetString()!)
                : outer;
            return payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("loomo", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "installExtension";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
