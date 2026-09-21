namespace sk0ya.Loomo.App.Services;

internal static class BrowserZoomPolicy
{
    public static double NextFactor(double current, int step)
        => step == 0 ? 1.0 : Math.Clamp(current + step * 0.1, 0.25, 4.0);

    public static int Percent(double factor) => (int)Math.Round(factor * 100);
}
