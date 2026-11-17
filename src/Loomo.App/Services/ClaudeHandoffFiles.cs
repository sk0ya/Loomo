namespace sk0ya.Loomo.App.Services;

/// <summary>Claude Code へ渡す長いもの（ターミナルの選択・コマンドの出力・PR の本文）を書く一時フォルダー
/// <c>%TEMP%\Loomo\handoff</c>。起動時に一日より古いファイルを掃除する（ファイルの中身は一時的なもの）。</summary>
public static class ClaudeHandoffFiles {
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "Loomo", "handoff");

    /// <summary>一日より前に書かれたファイルを消す。失敗は無視する（掃除のために起動を止めない）。</summary>
    public static void PruneOld(TimeSpan olderThan) {
        try {
            if (!Directory.Exists(Folder))
                return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(Folder)) {
                try {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
