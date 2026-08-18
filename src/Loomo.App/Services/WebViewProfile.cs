namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザペイン・EditorSupport・切り離しウィンドウが<b>共有する</b> WebView2 プロファイルの置き場所。
/// UserDataFolder が1つ＝ログインも拡張機能も全ペインで1つ、という設計（§21.5.1／§21.5.2）の一次情報はここ。
/// パスを二重に綴らないよう、この下にぶら下がるもの（プレビューページ、CDP ポートの控え）も併せて持つ。</summary>
internal static class WebViewProfile
{
    /// <summary>共有 UserDataFolder。</summary>
    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "WebView2");

    /// <summary>EditorSupport の一時ページを置くフォルダー（仮想ホストで公開する）。</summary>
    public static string PreviewPageFolder { get; } = Path.Combine(UserDataFolder, "preview-page");

    /// <summary>先行インスタンスが選んだ CDP ポートの控え（<see cref="WebViewDebugPort"/>）。
    /// プロファイルと寿命を揃えたいので UserDataFolder の隣に置く。</summary>
    public static string DebugPortRecord { get; } = Path.Combine(UserDataFolder, "debug-port");
}
