namespace sk0ya.Loomo.App.Services;

/// <summary>§31の操作時刻を採る診断専用ログ。通常起動ではI/Oを一切行わない。</summary>
internal static class IdeQualityDiagnosticLog
{
    public static readonly bool IsEnabled = string.Equals(
        Environment.GetEnvironmentVariable("LOOMO_IDE_QUALITY_DIAG"), "1", StringComparison.Ordinal);
    private static readonly string PathName = Path.Combine(Path.GetTempPath(), "loomo-ide-quality.log");
    private static readonly object Gate = new();

    public static void Write(string eventName, string detail)
    {
        if (!IsEnabled) return;
        try
        {
            lock (Gate)
                File.AppendAllText(PathName,
                    $"{DateTimeOffset.Now:O}\t{eventName}\t{detail}{Environment.NewLine}");
        }
        catch
        {
            // 計測失敗でIDE本体を止めない。
        }
    }
}
