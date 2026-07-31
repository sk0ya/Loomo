namespace sk0ya.Loomo.App.Views;
/// <summary>ブランチ切替コントロールの開閉。中身（同期帯・絞り込み・一覧・右クリックメニュー）は <see cref="BranchSwitcherView"/> が持ち、タイトルバーと Git ペインヘッダーはそれを同じように ポップアップへ載せるだけ。ここは「どのボタンでどのポップアップを開くか」に徹する（開閉のガードは <see cref="ShellWindow.TogglePopup"/>）。</summary>
public partial class ShellWindow {
    private void OnTitleBarBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(BranchPopup, BranchSwitcher);
    private void OnGitPaneBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(GitPaneBranchPopup, GitPaneBranchSwitcher);
    private void ToggleBranchPopup(Popup popup, BranchSwitcherView switcher)
        => TogglePopup(popup, () => {
            _vm.GitSession.EnsureLoaded();
            switcher.PrepareForOpen();
        });
    private void HookBranchSwitchers() {
        Hook(BranchPopup, BranchSwitcher);
        Hook(GitPaneBranchPopup, GitPaneBranchSwitcher);
        void Hook(Popup popup, BranchSwitcherView switcher) {
            switcher.CloseRequested += (_, _) => popup.IsOpen = false;
            TrackPopupClose(popup);
        }
    }
}
