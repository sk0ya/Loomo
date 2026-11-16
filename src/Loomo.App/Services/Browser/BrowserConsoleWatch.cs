using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.Services.Browser;

namespace sk0ya.Loomo.App.Services.Browser;

/// <summary>
/// WebView2 のページのコンソール（エラー・未処理の例外・警告）を DevTools の購読で溜める（§45.5 P2）。
/// ページのコードには何も足さない。<see cref="LoomoWebView2"/> の初期化のとき一度だけ <see cref="Attach"/> する。
/// </summary>
internal static class BrowserConsoleWatch
{
    private static readonly ConditionalWeakTable<CoreWebView2, BrowserConsoleLog> Logs = new();

    /// <summary>このブラウザのコンソールの記録（まだ繋いでいなければ空のもの）。</summary>
    public static BrowserConsoleLog LogFor(CoreWebView2 core) => Logs.GetValue(core, _ => new BrowserConsoleLog());

    public static void Attach(CoreWebView2 core)
    {
        var log = LogFor(core);
        core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (BrowserConsoleLog.ParseException(e.ParameterObjectAsJson, DateTimeOffset.Now) is { } entry)
                log.Add(entry);
        };
        core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (BrowserConsoleLog.ParseConsole(e.ParameterObjectAsJson, DateTimeOffset.Now) is { } entry)
                log.Add(entry);
        };
        // 購読の後で有効にする（有効にすると、それ以降のイベントが届く）。失敗しても表示は止めない。
        _ = Enable(core);
    }

    private static async Task Enable(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"ブラウザのコンソールを読めませんでした: {ex.Message}");
        }
    }
}
