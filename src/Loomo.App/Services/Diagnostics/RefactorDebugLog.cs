namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 「リファクタリングの候補が出ない」を切り分けるための開発時専用ログ。
/// 環境変数 <c>LOOMO_REFACTOR_DEBUG=1</c> のときだけ
/// <c>%APPDATA%/Loomo/refactor-debug.log</c> へ追記する（既定は完全に無効・I/O ゼロ）。
///
/// <para>この経路は失敗しても「候補がありません」としか出ないので、原因が
/// <b>選択が消えた／サーバー未接続／サーバーが0件を返した／分類で落とした</b>のどれなのかを
/// 外から見分けられない。ログはその4点だけを記録する（§32.4.1）。</para>
/// </summary>
internal static class RefactorDebugLog
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_REFACTOR_DEBUG"), "1", StringComparison.Ordinal);

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "refactor-debug.log");

    internal static bool IsEnabled => Enabled;

    internal static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
