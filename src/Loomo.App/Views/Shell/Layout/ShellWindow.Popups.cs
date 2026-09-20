namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ボタンから開くポップアップ（ブランチ切替・ワークスペース切替・ブラウザのツールバー）の開閉。中身は <see cref="PopupReopenGuard"/>（ペインの中のポップアップと共用）。</summary>
public partial class ShellWindow {
    /// <summary>ボタンのクリックでポップアップを開閉する。<paramref name="prepare"/> は開く直前に呼ぶ（中身の初期化）。</summary>
    private void TogglePopup(Popup popup, Action prepare) => PopupReopenGuard.Toggle(popup, prepare);
    /// <summary>ToggleButton 版のガード（<c>PreviewMouseLeftButtonUp</c> へ繋ぐこと）。</summary>
    private void SuppressPopupReopen(object sender, MouseButtonEventArgs e, Popup popup)
        => PopupReopenGuard.SuppressReopen(sender, e, popup);
    /// <summary>閉じた時刻を記録して再オープンガードを効かせる（各ポップアップにつき一度だけ呼ぶ）。</summary>
    private void TrackPopupClose(Popup popup) => PopupReopenGuard.Track(popup);
}
