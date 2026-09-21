namespace sk0ya.Loomo.App.Services;

/// <summary>ダイアログの入力欄で共有する空白処理。</summary>
internal static class DialogTextPolicy
{
    public static string Trim(string? text) => text?.Trim() ?? string.Empty;

    public static bool IsMissingRequiredText(string? text)
        => string.IsNullOrWhiteSpace(text);

    public static int NameSelectionLength(string initial, bool selectNameOnly)
    {
        var extensionSeparator = selectNameOnly ? initial.LastIndexOf('.') : -1;
        return extensionSeparator > 0 ? extensionSeparator : initial.Length;
    }

    public static string? TrimOrNull(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
