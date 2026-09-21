namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ボタンから開く <c>StaysOpen=False</c> のポップアップを、<b>同じボタンで閉じられる</b>ようにする
/// ガード。何もしないと「マウスダウンでポップアップが自分で閉じる→Click で開き直す」となり、
/// 押しても閉じないボタンになる。閉じた直後の再オープンだけを短時間抑えることで、その一往復を止める。
///
/// <para>持ち主は1つではない——ウィンドウのタイトルバー（ブランチ切替・ワークスペース切替・ブラウザの
/// ツールバー）と、ペインの中（コミット絞り込みの「📅 期間」）の両方が同じ組を使う。だから状態は
/// どちらかのクラスではなく<b>ポップアップ自身</b>に持たせる（辞書に溜めると、切り離して作り直した
/// ペインのポップアップがアプリ終了まで残る）。</para>
/// </summary>
internal static class PopupReopenGuard {
    private static readonly DependencyProperty ClosedAtProperty =
        DependencyProperty.RegisterAttached(
            "ClosedAt", typeof(DateTime), typeof(PopupReopenGuard), new PropertyMetadata(DateTime.MinValue));

    /// <summary>閉じた時刻を記録してガードを効かせる（各ポップアップにつき一度だけ呼ぶ）。</summary>
    public static void Track(Popup popup) {
        DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup))
            ?.AddValueChanged(popup, (_, _) => {
                if (!popup.IsOpen)
                    popup.SetValue(ClosedAtProperty, DateTime.UtcNow);
            });
    }

    private static bool JustClosed(Popup popup)
        => PopupReopenPolicy.WasClosedRecently(
            (DateTime)popup.GetValue(ClosedAtProperty), DateTime.UtcNow);

    /// <summary>ボタンのクリックでポップアップを開閉する。<paramref name="prepare"/> は開く直前に呼ぶ（中身の初期化）。</summary>
    public static void Toggle(Popup popup, Action prepare) {
        if (popup.IsOpen) {
            popup.IsOpen = false;
            return;
        }
        if (JustClosed(popup))
            return;
        prepare();
        popup.IsOpen = true;
    }

    /// <summary><c>IsChecked</c> と <c>IsOpen</c> を同じ旗へ TwoWay で結んだ ToggleButton 版のガード。
    /// <b>マウスアップで</b>受けるのが肝で、開いている最中の押し下げの実際の順番は（最小再現で計測）
    /// ①ポップアップが自分で閉じる（＝チェックも外れる）→②<c>MouseLeftButtonDown</c>（<b>バブルのみ</b>。
    /// トンネルの <c>PreviewMouseLeftButtonDown</c> はポップアップのキャプチャに食われてボタンまで来ない）
    /// →③<c>MouseLeftButtonUp</c>→④<c>Click</c> でチェックが戻り<b>開き直す</b>、というもの。
    /// ①〜③のうちボタンまで確実に届くのは③なので、そこで <c>Click</c> を止める。
    /// <para><b>繋ぐのは <c>PreviewMouseLeftButtonUp</c>（トンネル）でなければならない</b>——
    /// バブルの <c>MouseLeftButtonUp</c> は <c>ButtonBase</c> のクラスハンドラーが先に走って
    /// ④の <c>Click</c> をもう起こしてしまうので、同じ要素に付けた通常のハンドラーでは間に合わない。</para>
    /// <para>④を後から取り消す形にしないのは、一瞬でも開くと中身の読み込み
    /// （拡張機能の取り直し・保存済みログイン情報の復号）が走ってしまうため。</para></summary>
    public static void SuppressReopen(object sender, MouseButtonEventArgs e, Popup popup) {
        if (sender is not ToggleButton button)
            return;
        // 閉じたばかりでも開いてもいないなら、今回の押しは「開く」。ToggleButton の素の動きに任せる。
        if (!popup.IsOpen && !JustClosed(popup))
            return;
        popup.IsOpen = false;   // まだ開いているなら（自動クローズが後回しの経路）ここで閉じる
        e.Handled = true;       // Click を起こさせない＝チェックが戻って開き直すのを防ぐ
        // Click を止めたぶん、押し下げで掴んだマウスはこちらで返す（掴んだままだと次の操作を全部吸う）。
        if (button.IsMouseCaptured)
            button.ReleaseMouseCapture();
    }
}
