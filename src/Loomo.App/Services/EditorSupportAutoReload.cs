namespace sk0ya.Loomo.App.Services;

/// <summary>
/// プレビュー中のファイルがディスク上で変わったとき、EditorSupport を自動で読み直すかの判断（§24.8）。
/// <para>
/// <b>純ロジックだけをここに置く。</b>実際の監視（<see cref="SingleFileWatcher"/>）と読み直し
/// （<c>EditorSupportWebViewController.ReloadShowing</c>）はホスト側にあるが、「そもそも見張るのか」
/// 「届いた変更で読み直してよいのか」を決めているのはこの2つの関数で、そこが間違うと
/// <b>隠れたペインや別ファイルのために WebView2 を読み直す</b>ことになる。ホストの partial に埋めると
/// テストから触れないので外へ出してある（§26.9 で <c>EditorSupportPageState</c> を切り出したのと同じ理由）。
/// </para>
/// </summary>
public static class EditorSupportAutoReload
{
    /// <summary>
    /// いま見張るべきファイル（null＝見張らない）。条件は3つとも必要:
    /// ①ファイル直開きの提供者で ②その提供者が自動リロードを宣言していて
    /// （<see cref="IEditorSupportUriProvider.ReloadOnFileChange"/>）③ペインが表示されている。
    /// <para>
    /// ペインが見えていないなら見張らない——<b>取りこぼしより「張りっぱなし」を避ける</b>のがここの方針。
    /// 見えていない間の更新は、次に表示されたときの通常の描画で拾われる。
    /// </para>
    /// </summary>
    public static string? WatchTarget(
        IEditorSupportProvider? provider, string? filePath, bool paneShowing)
        => paneShowing
           && !string.IsNullOrWhiteSpace(filePath)
           && provider is IEditorSupportUriProvider { ReloadOnFileChange: true }
            ? filePath
            : null;

    /// <summary>
    /// 変更通知が UI スレッドへ届いた時点で、実際に読み直してよいか。
    /// <b>判断材料はそのときの状態から取り直す</b>——監視を張ってから通知が届くまでの間に、
    /// 追従先のファイルが変わったりペインが隠れたりする（デバウンスぶんは必ず遅れる）。
    /// </summary>
    public static bool ShouldReload(
        string? changedPath, IEditorSupportProvider? provider, string? filePath, bool paneShowing)
        => changedPath is not null
           && WatchTarget(provider, filePath, paneShowing) is { } target
           && PathEquals(changedPath, target);

    /// <summary>同じファイルを指しているか（相対・大文字小文字の違いを吸収する）。</summary>
    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
