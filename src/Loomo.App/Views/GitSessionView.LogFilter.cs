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

    /// <summary>一覧の "/" からの着地点。打ち直しが前の語の後ろへ続かないよう全選択して渡す。</summary>
    private void FocusLogFilter()
    {
        LogFilterBox.Focus();
        LogFilterBox.SelectAll();
    }

    /// <summary>
    /// 絞り込み欄から一覧へ戻る道。Esc は語があれば消し、無ければ一覧へ返す。
    /// <b>Esc は必ず握る</b>——TextBox の既定の Esc は Undo で、握らないと
    /// <c>ApplicationCommands.Undo</c> が昇っていき、編集していないエディタの取り消しまで巻き込む。
    /// </summary>
    private void OnLogFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (box.Text.Length > 0)
                {
                    box.Clear();
                    // Delay=200 を待たずに反映する（Esc の手応えを遅らせない）
                    box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                }
                else
                {
                    FocusCommitList();
                }
                break;
            case Key.Down:
            case Key.Enter:
                e.Handled = true;
                FocusCommitList();
                break;
        }
    }

    private void OnLogDateToggle(object sender, MouseButtonEventArgs e)
        => PopupReopenGuard.SuppressReopen(sender, e, LogDatePopup);
}
