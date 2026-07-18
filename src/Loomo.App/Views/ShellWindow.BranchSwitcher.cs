
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ブランチ切替コントロールの開閉。中身（同期帯・絞り込み・一覧・右クリックメニュー）は
/// <see cref="BranchSwitcherView"/> が持ち、タイトルバーと Git ペインヘッダーはそれを同じように
/// ポップアップへ載せるだけ。ここは「どのボタンでどのポップアップを開くか」に徹する。
/// </summary>
public partial class ShellWindow
{
    // 各ポップアップが最後に閉じた時刻。StaysOpen="False" のポップアップは、開いている間に 元のボタンを押すと、それを「外側クリック」とみなしてマウス押下で先に自分で閉じ、その後 ボタンの Click が来る。素直に Popup.IsOpen を見て開き直すと押しても閉じない （＝トグルにならない）ので、閉じた直後の Click は「閉じる操作だったぶん」として捨てる。
    private readonly Dictionary<Popup, DateTime> _branchPopupClosedAt = new();

    // 閉じた直後の Click を同じ操作の一部とみなす猶予。実測では押下→Click が約45ms （Popup.Closed はさらに遅れて発火するうえ束ねられるので、時刻の記録には使えない）。
    private static readonly TimeSpan BranchPopupReopenGuard = TimeSpan.FromMilliseconds(250);

    private void OnTitleBarBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(BranchPopup, BranchSwitcher);

    private void OnGitPaneBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(GitPaneBranchPopup, GitPaneBranchSwitcher);

    private void ToggleBranchPopup(Popup popup, BranchSwitcherView switcher)
    {
        if (popup.IsOpen)
        {
            popup.IsOpen = false;
            return;
        }

        if (_branchPopupClosedAt.TryGetValue(popup, out var closedAt)
            && DateTime.UtcNow - closedAt < BranchPopupReopenGuard)
        {
            return;
        }

        // 通常は起動時の遅延読込で済んでいるが、未読込でも開いた瞬間に一覧が出るよう保険をかける
        _vm.GitSession.EnsureLoaded();
        switcher.PrepareForOpen();
        popup.IsOpen = true;
    }

    // 中身側から閉じたい（チェックアウト成功・ダイアログを出す前）ときの受け口と、 トグル判定用の「閉じた時刻」の記録をまとめて配線する。
    private void HookBranchSwitchers()
    {
        Hook(BranchPopup, BranchSwitcher);
        Hook(GitPaneBranchPopup, GitPaneBranchSwitcher);

        void Hook(Popup popup, BranchSwitcherView switcher)
        {
            switcher.CloseRequested += (_, _) => popup.IsOpen = false;

            // Closed ではなく IsOpen の変化を見る：Closed は押下から数十 ms 遅れて発火し、 しかも束ねられて出ないことがある（＝Click の時点ではまだ「閉じた」ことが分からない）。
            DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup))
                ?.AddValueChanged(popup, (_, _) =>
                {
                    if (!popup.IsOpen)
                        _branchPopupClosedAt[popup] = DateTime.UtcNow;
                });
        }
    }
}
