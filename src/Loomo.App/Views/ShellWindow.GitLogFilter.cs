namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: Git ペインのログ絞り込みの「📅 期間」ドロップダウン。
/// ブラウザのツールバーと同じ「ToggleButton＋<c>StaysOpen=False</c> のポップアップを同じ旗へ TwoWay」の組なので、
/// 何もしないと同じように<b>自分のボタンでは閉じられない</b>——理由と押し下げの実際の順番は
/// <see cref="SuppressPopupReopen"/> にある。</summary>
public partial class ShellWindow {
    private void InitializeGitLogFilter() => TrackPopupClose(LogDatePopup);
    private void OnLogDateToggle(object sender, MouseButtonEventArgs e) => SuppressPopupReopen(sender, e, LogDatePopup);
}
