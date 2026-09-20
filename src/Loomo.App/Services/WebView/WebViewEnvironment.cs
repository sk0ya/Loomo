namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザペイン・EditorSupport・切り離しウィンドウが共有する WebView2 環境の生成条件（プロファイルと
/// ブラウザ引数）と、生成に失敗したときの立て直し。生成条件を綴る場所はここ<b>1か所</b>——同じ UserDataFolder に
/// 入る全員が同一引数である必要があり、それはプロセスをまたいでも要るため（§21.5.1／§21.5.3）。</summary>
internal static class WebViewEnvironment
{
    private static int _created;
    private static int _reported;

    /// <summary>全 WebView2 共通の生成プロパティ。</summary>
    public static CoreWebView2CreationProperties CreateProperties()
        => new()
        {
            UserDataFolder = WebViewProfile.UserDataFolder,
            // リモートデバッグポート付き（TS IDE のフロントデバッグがこのペインへ CDP アタッチする。§29）。
            // 番号は先行インスタンスと必ず揃える（WebViewDebugPort）。
            AdditionalBrowserArguments = "--allow-file-access-from-files " + WebViewDebugPort.Argument,
            // 拡張機能はプロファイル単位の設定なので、ここで一度立てればブラウザペインのタブも
            // 切り離しウィンドウも同じ顔ぶれになる。既定は false で、false のままだと
            // AddBrowserExtensionAsync は ERROR_NOT_SUPPORTED で失敗する（§21.5.2）。
            AreBrowserExtensionsEnabled = true,
        };

    /// <summary>WebView2 を作れた。以後は引数を変えてはいけない（同一プロセス内でも「同一引数」の縛りは効く）。</summary>
    public static void NoteCreated() => Interlocked.Exchange(ref _created, 1);

    /// <summary>生成失敗からの立て直し。同じプロファイルを別の引数のブラウザプロセスが握っているのが唯一の
    /// 現実的な原因なので、いま動いている番号へ合わせ直す。true なら呼び元は WebView2 を<b>作り直して</b>
    /// 一度だけ再試行してよい（失敗した WebView2 コントロールは使い回さない）。</summary>
    public static bool TryRecover()
        => Volatile.Read(ref _created) == 0 && WebViewDebugPort.TryAdoptRunningPort();

    /// <summary>立て直しても駄目だったときに一度だけ知らせる。<b>黙って無反応にしない</b>のがここの役目——
    /// 失敗を握り潰していたせいで、症状が「ブラウザとエディタ支援だけ何も起きない」になり原因に辿り着けなかった。</summary>
    public static void ReportUnavailable(string pane)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0)
            return;
        ToastService.Error(
            $"{pane}を表示できません（WebView2 を初期化できませんでした）。"
            + "別の Loomo が同じブラウザプロファイルを違う設定で使っている場合は、片方を終了すると直ります。");
    }
}
