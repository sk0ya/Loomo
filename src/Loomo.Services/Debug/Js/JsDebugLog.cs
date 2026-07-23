using System;
using System.IO;

namespace sk0ya.Loomo.Services.Debug.Js;

/// <summary>js-debug セッションの診断ログ。<see cref="DapConnection"/> と同じファイル
/// （<c>%TEMP%\loomo-dap-debug.log</c>）へ流し、プロトコルログと時系列で突き合わせられるようにする。</summary>
internal static class JsDebugLog
{
    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "loomo-dap-debug.log");

    public static void Write(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] js: {msg}\n"); } catch { }
    }
}
