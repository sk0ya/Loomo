using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>可視ブラウザペイン（WebView2）を js-debug から CDP アタッチできるようにするためのリモートデバッグポート。
/// WebView2 は UserDataFolder を共有する全コントロールが<b>同一のブラウザ引数</b>である必要があるため、
/// 生成プロパティ（<c>CreateWebViewCreationProperties</c>）に一度だけ <c>--remote-debugging-port=&lt;N&gt;</c> を付け、
/// 全ペインが 1 つの共有ブラウザプロセス＝1 つの CDP エンドポイント（127.0.0.1:N のみ）を露出する。
/// フロントデバッグ（TS IDE）はこのポートへ <c>pwa-chrome</c> の attach を張り、ペインそのものをデバッグする。</summary>
internal static class WebViewDebugPort
{
    private static int? _port;

    /// <summary>CDP ポート（起動時に一度だけ空きを選ぶ。9333 起点＝一般的な dev ポートと衝突しにくい帯）。
    /// localhost にのみバインドされる。</summary>
    public static int Port => _port ??= DevServerPortUtil.FindFreePort(9333);

    /// <summary>WebView2 生成引数に足すリモートデバッグ指定。</summary>
    public static string Argument => $"--remote-debugging-port={Port}";
}
