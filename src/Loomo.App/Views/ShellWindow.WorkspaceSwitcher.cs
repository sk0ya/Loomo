namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: タイトルバーのワークスペース切替ポップアップの開閉と、中身（<see cref="WorkspaceSwitcherView"/>）から上がってくる「ウィンドウ側でしかできない操作」の受け口——フォルダー追加（FolderTree を持つのはここ）と削除の確認。</summary>
public partial class ShellWindow {
    private void OnTitleBarWorkspaceClick(object sender, RoutedEventArgs e)
        => TogglePopup(WorkspacePopup, WorkspaceSwitcher.PrepareForOpen);
    private void HookWorkspaceSwitcher() {
        WorkspaceSwitcher.CloseRequested += (_, _) => WorkspacePopup.IsOpen = false;
        WorkspaceSwitcher.AddFolderRequested += (_, path) => _vm.FolderTree.AddFolderToWorkspace(path);
        WorkspaceSwitcher.RemoveRequested += (_, entry) => RemoveWorkspaceWithConfirm(entry);
        TrackPopupClose(WorkspacePopup);
    }
    /// <summary>一覧からの削除。フォルダ自体は消さないが、そのワークスペースのタブ・レイアウトの保存状態は
    /// 失われるので確認する。最後の1つは常にアクティブが要るため削除できない。</summary>
    private void RemoveWorkspaceWithConfirm(WorkspaceEntryViewModel entry) {
        if (!_vm.Workspaces.RemoveWorkspaceCommand.CanExecute(entry)) {
            ToastService.Info("最後のワークスペースは削除できません（常に1つは開いている必要があります）。");
            return;
        }
        var result = MessageBox.Show( this, $"ワークスペース「{entry.Label}」を一覧から削除しますか？\n" +
            "フォルダ自体は削除されません（タブ・レイアウトの保存状態は失われます）。", "ワークスペースの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
            _vm.Workspaces.RemoveWorkspaceCommand.Execute(entry);
    }
}
