using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ペインのリサイズがドラッグ直後に元へ戻る不具合を追跡するための一時的な診断ログ。
/// 環境変数 <c>LOOMO_PANE_DEBUG=1</c> のときだけ <c>%APPDATA%/Loomo/panelayout-debug.log</c> へ即時追記する
/// （既定は無効＝ノーオーバーヘッド）。原因が特定でき次第、呼び出し箇所ごと削除してよい。
/// </summary>
internal static class PaneLayoutDebugLog
{
    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_PANE_DEBUG"), "1", StringComparison.Ordinal);

    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "panelayout-debug.log");
    private static bool _started;

    /// <summary>1行記録する。<paramref name="withCaller"/>=true なら直近の呼び出し元チェーンも添える
    /// （どの経路から RebuildPaneLayout / Width 変更が起きたかを見分けるため）。</summary>
    public static void Log(string message, bool withCaller = false)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (!_started)
                {
                    _started = true;
                    File.WriteAllText(LogPath, $"=== pane layout debug {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n");
                }
                var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
                if (withCaller)
                {
                    var frames = new StackTrace(1, false).GetFrames();
                    if (frames is not null)
                    {
                        var names = frames.Take(8)
                            .Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?");
                        line += "\n    from: " + string.Join(" <- ", names);
                    }
                }
                File.AppendAllText(LogPath, line + "\n");
            }
            catch { /* 診断用。失敗は無視 */ }
        }
    }
}
