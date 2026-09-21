namespace sk0ya.Loomo.App.Services;

internal readonly record struct BrowserExtensionPopupSize(double Width, double Height);

internal static class BrowserExtensionPopupLayout
{
    public static bool TryParseSize(string json, out BrowserExtensionPopupSize size)
    {
        if (JsonSerializer.Deserialize<double[]>(json) is not { Length: 2 } values)
        {
            size = default;
            return false;
        }

        size = new(
            Math.Clamp(values[0], 240, 800),
            Math.Clamp(values[1], 120, 640));
        return true;
    }
}
