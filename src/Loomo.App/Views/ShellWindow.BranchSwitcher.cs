namespace sk0ya.Loomo.App.Views;
/// <summary>タイトルバーのブランチ切替ポップアップの開閉。中身（同期帯・絞り込み・一覧・右クリックメニュー）は <see cref="BranchSwitcherView"/> が持つ。ここは「ボタンでポップアップを開く」だけ（開閉のガードは <see cref="ShellWindow.TogglePopup"/>）。Git ペインには同じポップアップを置かない——あちらはブランチ一覧が常に見えていて（<see cref="GitSessionView"/> の BranchPanel）、切替も同期もその場でできる。</summary>
public partial class ShellWindow {
    private void OnTitleBarBranchClick(object sender, RoutedEventArgs e)
        => TogglePopup(BranchPopup, () => {
            _vm.GitSession.EnsureLoaded();
            BranchSwitcher.PrepareForOpen();
        });
    private void HookBranchSwitchers() {
        BranchSwitcher.CloseRequested += (_, _) => BranchPopup.IsOpen = false;
        TrackPopupClose(BranchPopup);
    }
}
