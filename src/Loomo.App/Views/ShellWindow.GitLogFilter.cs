namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: Git ペインのログ絞り込みの「📅 期間」ドロップダウン。
/// ブラウザのツールバーと同じ「ToggleButton＋<c>StaysOpen=False</c> のポップアップを同じ旗へ TwoWay」の組なので、
/// 何もしないと同じように<b>自分のボタンでは閉じられない</b>——理由と押し下げの実際の順番は
/// <see cref="SuppressPopupReopen"/> にある。
///
/// <para>あわせて、一覧の "/" から絞り込み欄へ移る導線と、そこから戻る出口もここで繋ぐ。絞り込み欄は
/// ペインの中ではなく<b>ペインヘッダー（この ShellWindow）</b>に住んでいるので、GitSessionView からは触れない。</para></summary>
public partial class ShellWindow {
    private void InitializeGitLogFilter() {
        TrackPopupClose(LogDatePopup);
        GitSessionHost.LogFilterFocusRequested += (_, _) => {
            LogFilterBox.Focus();
            LogFilterBox.SelectAll();   // 打ち直しが前の語の後ろへ続かないように
        };
    }

    /// <summary>
    /// 絞り込み欄から一覧へ戻る道。Esc は語があれば消し、無ければ一覧へ返す。
    /// <b>Esc は必ず握る</b>——TextBox の既定の Esc は Undo で、握らないと
    /// <c>ApplicationCommands.Undo</c> が昇っていき、編集していないエディタの取り消しまで巻き込む。
    /// </summary>
    private void OnLogFilterKeyDown(object sender, KeyEventArgs e) {
        if (sender is not TextBox box) return;
        switch (e.Key) {
            case Key.Escape:
                e.Handled = true;
                if (box.Text.Length > 0) {
                    box.Clear();
                    // Delay=200 を待たずに反映する（Esc の手応えを遅らせない）
                    box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                } else {
                    GitSessionHost.FocusCommitList();
                }
                break;
            case Key.Down:
            case Key.Enter:
                e.Handled = true;
                GitSessionHost.FocusCommitList();
                break;
        }
    }

    private void OnLogDateToggle(object sender, MouseButtonEventArgs e) => SuppressPopupReopen(sender, e, LogDatePopup);
}
