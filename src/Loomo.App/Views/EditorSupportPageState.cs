namespace sk0ya.Loomo.App.Views;

/// <summary>読み込みが終わったときに、ホストへ次に頼むこと。</summary>
internal enum EditorSupportPageAction
{
    /// <summary>何もしない（表示は成立している）。</summary>
    None,

    /// <summary>ページ全体を組み直してほしい（失敗・応答なし・初回描画の取りこぼし）。</summary>
    RequestReload,
}

/// <summary>
/// EditorSupport の WebView2 に「いま何が載っているか」だけを持つ状態機械。
/// <see cref="EditorSupportWebViewController"/> から切り出してあるのは、<b>固まる／固まらないを
/// 決めているのがここだから</b>——本文差し替えが成り立つ条件も、失敗をどこで打ち切るかも、
/// すべてこの遷移で決まるのに、<c>CoreWebView2</c> を抱えたままではテストから一切触れなかった。
/// WebView2 の呼び出し（Navigate・PostWebMessage）とタイマーは呼び元に残す。
/// </summary>
internal sealed class EditorSupportPageState
{
    private EditorSupportPageId? _pageId;
    private EditorSupportPageStatus _status = EditorSupportPageStatus.Idle;
    /// <summary>同じページの二度目の失敗で再試行を打ち切るための記憶。</summary>
    private EditorSupportPageId? _lastFailedId;
    private bool _firstRenderHealed;

    public EditorSupportPageStatus Status => _status;

    public string? CurrentUri => _pageId?.Uri;

    /// <summary>
    /// 本文差し替えで更新できるページの鍵。<b>読み込みが完了しているときだけ</b>返す。
    /// <c>Loading</c>／<c>Failed</c>／<c>Idle</c> では null＝呼び元は必ずページ全体を組み立てる。
    /// </summary>
    public string? ReadyPageKey
        => _status == EditorSupportPageStatus.Ready ? _pageId?.PageKey : null;

    /// <summary>
    /// この URI は再ナビゲートを省けるか。省けるのは「その URI を読み<b>終えている</b>」ときだけ。
    /// Loading/Failed は必ずやり直す（以前は「ナビゲートを投げた」だけで省いていたため、
    /// 失敗した PDF などがガードに引っかかって永久に読み直されなかった）。
    /// </summary>
    public bool IsShowing(string uri)
        => _status == EditorSupportPageStatus.Ready
           && string.Equals(_pageId?.Uri, uri, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 本文差し替えが成り立つか。成り立つのは「この本文が想定していたページが、いま読み込み済みで
    /// 載っている」ときだけ。組み立て中（await 中）に別のページへ遷移していたら、投げても何も起きずに
    /// 古い表示のまま固まる。
    /// </summary>
    public bool CanPatchBody(string? pageKey)
        => _status == EditorSupportPageStatus.Ready
           && _pageId is { PageKey: { } current }
           && pageKey is not null
           && current == pageKey;

    /// <summary>ナビゲーションを始める。</summary>
    public void BeginLoad(EditorSupportPageId id)
    {
        if (_lastFailedId is not null && _lastFailedId != id)
            _lastFailedId = null;   // 別のページへ移った：前のページの失敗記憶は持ち越さない
        _pageId = id;
        _status = EditorSupportPageStatus.Loading;
    }

    /// <summary>読み込みが成立しなかった。<b>同一性を必ず捨てる</b>ので、次の要求は必ず作り直しになる。</summary>
    public void Fail()
    {
        _pageId = null;
        _status = EditorSupportPageStatus.Failed;
    }

    /// <summary>ナビゲーション完了。戻り値はホストへ頼むこと。</summary>
    public EditorSupportPageAction Completed(bool success)
    {
        var attempted = _pageId;
        if (success)
        {
            _status = EditorSupportPageStatus.Ready;
            _lastFailedId = null;
        }
        else
        {
            Fail();
        }

        // WebView2 は最初のページを載せても描画が出てこないことがある（コンポジション初期化との競合）。
        // 一度だけ組み直しを頼んで実描画を確定させる。二度目以降はフラグで止まる。
        if (!_firstRenderHealed)
        {
            _firstRenderHealed = true;
            return EditorSupportPageAction.RequestReload;
        }

        // 失敗したまま放置すると空白のページが残るので組み直しを頼む（同一性は Fail で捨ててある）。
        return !success && ShouldRetryAfterFailure(attempted)
            ? EditorSupportPageAction.RequestReload
            : EditorSupportPageAction.None;
    }

    /// <summary>
    /// 応答なしの見張りが鳴った。<c>Loading</c> のままなら失敗として畳む。
    /// WebView2 の完了イベントは（プロセス落ち・不正 URI・描画中断などで）<b>来ないことがある</b>。
    /// 以前は来なければ状態が <c>Loading</c> のまま固まり、同じページを二度と読み直さなかった。
    /// </summary>
    public EditorSupportPageAction WatchdogFired()
    {
        if (_status != EditorSupportPageStatus.Loading)
            return EditorSupportPageAction.None;
        var attempted = _pageId;
        Fail();
        return ShouldRetryAfterFailure(attempted)
            ? EditorSupportPageAction.RequestReload
            : EditorSupportPageAction.None;
    }

    /// <summary>載せているページを忘れる（ビューの張り替え・破棄）。</summary>
    public void Reset()
    {
        _pageId = null;
        _lastFailedId = null;
        _status = EditorSupportPageStatus.Idle;
    }

    /// <summary>次に張り直すビューでも初回描画の取りこぼしを直す。</summary>
    public void ResetFirstRenderHealing() => _firstRenderHealed = false;

    /// <summary>
    /// 失敗したページを組み直させてよいか。<b>同じページの二度目の失敗では false</b>——
    /// 開けない PDF などを相手に「失敗 → 再ナビゲート → また失敗」を延々と繰り返さないため。
    /// 別のページを要求すれば（<see cref="BeginLoad"/>）記憶は消え、また一度だけやり直す。
    /// </summary>
    private bool ShouldRetryAfterFailure(EditorSupportPageId? failed)
    {
        if (_lastFailedId == failed)
            return false;
        _lastFailedId = failed;
        return true;
    }
}
