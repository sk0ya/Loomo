namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 切り離しブラウザがいま見ているURL。WebView2のCore生成中はSourceを読めないため、
/// 切り離しの連続操作やスナップショット保存に備えて、最後に読めた遷移先をWPFホストへ保持する。
/// </summary>
internal sealed class SpinoffBrowserAddress(string initial)
{
    /// <summary>スナップショット保存側がまだ生成中のWebViewより先にURLを取得できるようにする。</summary>
    private static readonly DependencyProperty AddressProperty = DependencyProperty.RegisterAttached(
        "Address", typeof(SpinoffBrowserAddress), typeof(SpinoffBrowserAddress));

    public void AttachTo(DependencyObject host) => host.SetValue(AddressProperty, this);

    public static SpinoffBrowserAddress? Of(DependencyObject? host)
        => host?.GetValue(AddressProperty) as SpinoffBrowserAddress;

    /// <summary>実体から読めた最後のURL（まだ一度も読めていなければ初期値）。</summary>
    public string Value { get; private set; } = initial;

    /// <summary>空でないURLだけを取り込み、生成中や異常終了による空値で行き先を消さない。</summary>
    public void Note(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            Value = url;
    }
}
