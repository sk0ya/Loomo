namespace sk0ya.Loomo.App.Views;

/// <summary>
/// GitSessionView: コミット一覧の上に載せた絞り込み帯（入力欄・作者・「📅 期間」）の操作。
///
/// <para>帯は<b>絞る相手と同じ列の中</b>に居る（以前はペインヘッダー＝ShellWindow 側で、
/// 一覧から "/" で飛ぶのにイベントを1つ挟み、狭いときは別の帯へ移し替える仕掛けまで要った）。
/// 同じビューの中なので、入る導線も出る導線もここで完結する。</para>
///
/// <para>「期間」はブラウザのツールバーと同じ「ToggleButton＋<c>StaysOpen=False</c> のポップアップを
/// 同じ旗へ TwoWay」の組なので、何もしないと<b>自分のボタンでは閉じられない</b>——理由と押し下げの
/// 実際の順番は <see cref="PopupReopenGuard.SuppressReopen"/> にある。</para>
/// </summary>
public partial class GitSessionView
{
    private void SetupLogFilter() => PopupReopenGuard.Track(LogDatePopup);

    private void OnLogFilterKeyDown(object sender, KeyEventArgs e)
        => _keyboardController.OnLogFilterKeyDown(sender, e);

    private void OnLogDateToggle(object sender, MouseButtonEventArgs e)
        => PopupReopenGuard.SuppressReopen(sender, e, LogDatePopup);
}
