using System.IO;

namespace sk0ya.Loomo.Pty.Host;

/// <summary>
/// ホストの記録（%LOCALAPPDATA%\Loomo\ptyhost\host.log）。窓を持たない常駐プロセスなので、
/// 起動・接続の失敗・異常終了を残しておかないと、後から何が起きたか辿れない。大きくなったら頭から捨てる。
/// </summary>
internal static class HostLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "ptyhost", "host.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 記録できないことでホストを止めない。
        }
    }
}
