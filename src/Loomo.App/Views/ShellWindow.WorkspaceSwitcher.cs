namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: タイトルバーのワークスペース切替ポップアップの開閉と、中身（<see cref="WorkspaceSwitcherView"/>）から上がってくる「ウィンドウ側でしかできない操作」の受け口——フォルダー追加（FolderTree を持つのはここ）と削除の確認。</summary>
public partial class ShellWindow {
    private void OnTitleBarWorkspaceClick(object sender, RoutedEventArgs e)
        => TogglePopup(WorkspacePopup, WorkspaceSwitcher.PrepareForOpen);
    private void HookWorkspaceSwitcher() {
        WorkspaceSwitcher.CloseRequested += (_, _) => WorkspacePopup.IsOpen = false;
        WorkspaceSwitcher.AddFolderRequested += (_, path) => AddFolderToActiveWorkspace(path);
        WorkspaceSwitcher.RemoveRequested += (_, entry) => RemoveWorkspaceWithConfirm(entry);
        // アクティブなワークスペースのフォルダー削除は、生きているツリー（WorkspaceService）を通す。
        // スナップショットへの反映は RootStateChanged → SaveActiveWorkspaceSnapshot の既存経路に乗る。
        _vm.Workspaces.FolderRemoveRequested += (_, path) => _vm.FolderTree.RemoveFolderFromWorkspace(path);
        TrackPopupClose(WorkspacePopup);
    }
    /// <summary>フォルダーをアクティブなワークスペースへ追加する。既存フォルダーと同じ・祖先/子孫の
    /// パスは <see cref="sk0ya.Loomo.Core.Abstractions.IWorkspaceService.AddFolder"/> が捨てるので、
    /// 黙って何も起きたように見えないよう理由を出す。</summary>
    private void AddFolderToActiveWorkspace(string path) {
        if (!_vm.FolderTree.AddFolderToWorkspace(path))
            ToastService.Info($"「{System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))}」は追加しませんでした（既にワークスペースに含まれるフォルダーです）。");
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
