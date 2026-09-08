namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 切り離しブラウザが<b>いま見ている URL</b>。器（Grid）の中身を作り直すときの行き先で、
/// 実体（WebView2）より長生きする必要があるので器の外に置く。
///
/// <para>行き先は本来 <c>CoreWebView2.Source</c> が正本だが、実体を<b>作っている最中は読めない</b>
/// （生成に1秒ほどかかる）。作り直しの合図はその最中にも届く——別の切り離し窓へ続けて2度移すと、
/// 1度目の実体がまだ生成中のまま2度目の作り直しが走る。そこで「読めた最後の URL」をここに残し、
/// 読めない間の行き先にする。これが無いと、切り離したときの URL まで巻き戻って、それ以降に
/// 見ていたページが黙って捨てられる。</para>
/// </summary>
internal sealed class SpinoffBrowserAddress(string initial)
{
    /// <summary>器（Grid）から行き先を引けるようにする添付プロパティ。切り離し直後の
    /// スナップショット保存は実体の生成（約1秒）より<b>先</b>に走るので、実体から読めない間の
    /// 行き先がここに要る——無いと URL 未設定で保存され、復元で既定ページへ戻ってしまう。</summary>
    private static readonly DependencyProperty AddressProperty = DependencyProperty.RegisterAttached(
        "Address", typeof(SpinoffBrowserAddress), typeof(SpinoffBrowserAddress));

    public void AttachTo(DependencyObject host) => host.SetValue(AddressProperty, this);

    public static SpinoffBrowserAddress? Of(DependencyObject? host)
        => host?.GetValue(AddressProperty) as SpinoffBrowserAddress;

    /// <summary>作り直しの行き先。実体から読めた最後の URL（まだ一度も読めていなければ初期値）。</summary>
    public string Value { get; private set; } = initial;

    /// <summary>実体から読めた URL を取り込む。読めなかった（null／空）ときは今の値を保つ——
    /// 生成中・落ちた後の「読めない」を行き先の消失として扱わないため。</summary>
    public void Note(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            Value = url;
    }
}
