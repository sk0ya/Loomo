namespace sk0ya.Loomo.App.Views;
/// <summary>ブランチ切替コントロールの開閉。中身（同期帯・絞り込み・一覧・右クリックメニュー）は <see cref="BranchSwitcherView"/> が持ち、タイトルバーと Git ペインヘッダーはそれを同じように ポップアップへ載せるだけ。ここは「どのボタンでどのポップアップを開くか」に徹する。</summary>
public partial class ShellWindow {
    private readonly Dictionary<Popup, DateTime> _branchPopupClosedAt = new();
    private static readonly TimeSpan BranchPopupReopenGuard = TimeSpan.FromMilliseconds(250);
    private void OnTitleBarBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(BranchPopup, BranchSwitcher);
    private void OnGitPaneBranchClick(object sender, RoutedEventArgs e)
        => ToggleBranchPopup(GitPaneBranchPopup, GitPaneBranchSwitcher);
    private void ToggleBranchPopup(Popup popup, BranchSwitcherView switcher) {
        if (popup.IsOpen) {
            popup.IsOpen = false;
            return;
        }
        if (_branchPopupClosedAt.TryGetValue(popup, out var closedAt)
            && DateTime.UtcNow - closedAt < BranchPopupReopenGuard) {
            return;
        }
        _vm.GitSession.EnsureLoaded();
        switcher.PrepareForOpen();
        popup.IsOpen = true;
    }
    private void HookBranchSwitchers() {
        Hook(BranchPopup, BranchSwitcher);
        Hook(GitPaneBranchPopup, GitPaneBranchSwitcher);
        void Hook(Popup popup, BranchSwitcherView switcher) {
            switcher.CloseRequested += (_, _) => popup.IsOpen = false;
            DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup))
                ?.AddValueChanged(popup, (_, _) => {
                    if (!popup.IsOpen)
                        _branchPopupClosedAt[popup] = DateTime.UtcNow;
                });
        }
    }
}
