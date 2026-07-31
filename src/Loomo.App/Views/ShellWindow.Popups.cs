namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: タイトルバー等のボタンから開くポップアップ（ブランチ切替・ワークスペース切替）の開閉。<c>StaysOpen=False</c> のポップアップは、開いている最中にボタンを押すと「マウスダウンで閉じる→Click で開き直す」となりトグルにならないので、閉じた直後の再オープンだけ短時間抑える。</summary>
public partial class ShellWindow {
    private readonly Dictionary<Popup, DateTime> _popupClosedAt = new();
    private static readonly TimeSpan PopupReopenGuard = TimeSpan.FromMilliseconds(250);
    /// <summary>ボタンのクリックでポップアップを開閉する。<paramref name="prepare"/> は開く直前に呼ぶ（中身の初期化）。</summary>
    private void TogglePopup(Popup popup, Action prepare) {
        if (popup.IsOpen) {
            popup.IsOpen = false;
            return;
        }
        if (_popupClosedAt.TryGetValue(popup, out var closedAt)
            && DateTime.UtcNow - closedAt < PopupReopenGuard) {
            return;
        }
        prepare();
        popup.IsOpen = true;
    }
    /// <summary>閉じた時刻を記録して再オープンガードを効かせる（各ポップアップにつき一度だけ呼ぶ）。</summary>
    private void TrackPopupClose(Popup popup) {
        DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup))
            ?.AddValueChanged(popup, (_, _) => {
                if (!popup.IsOpen)
                    _popupClosedAt[popup] = DateTime.UtcNow;
            });
    }
}
