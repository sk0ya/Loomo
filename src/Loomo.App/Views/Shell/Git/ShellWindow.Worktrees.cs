using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Git ペインのワークツリーを<b>部屋のどこへ</b>開くか。ワークスペース（部屋）ごと移る／
/// 今の部屋にフォルダーとして足す／表示ターミナルをそこへ移す／エクスプローラー、の4つ。
/// どれも部屋の配置に関わるので VM ではなくここが決める（§24.5.2 の「行き先はホストが持つ」と同じ）。
/// </summary>
public partial class ShellWindow {
    private void WireWorktreeRequests() {
        _vm.GitSession.WorktreeOpenRequested += (_, request) => OpenWorktree(request);
        _vm.GitSession.WorktreeRemoving += (_, path) => DetachWorktreeFolders(path);
    }

    private void OpenWorktree(GitWorktreeOpenRequest request) {
        var path = request.Path;
        switch (request.Mode) {
            case GitWorktreeOpenMode.Workspace:
                // 一覧に無ければ新しい部屋として足してから移る（ActivateFolder が両方をやる）。
                _vm.Workspaces.ActivateFolder(path);
                break;
            case GitWorktreeOpenMode.AddFolder:
                if (_workspace.Folders.Any(f => WorkspacePaths.IsWithin(f, path) && WorkspacePaths.IsWithin(path, f)))
                    return;   // もう入っている
                if (!_vm.FolderTree.AddFolderToWorkspace(path))
                    MessageBox.Show(this,
                        "このワークツリーはワークスペースのフォルダーと親子関係にあるため、フォルダーとして追加できません。\n" +
                        "（リポジトリの中に置いたワークツリーは「ワークスペースとして開く」を使ってください）",
                        "フォルダーとして追加", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case GitWorktreeOpenMode.Terminal:
                // エクスプローラーの「ターミナルでここへ」と同じ道（表示ターミナルへ送るのは移動だけ）。
                OnSetInTerminalRequested(this, new TerminalSetRequest(path, IsDirectory: true));
                break;
            case GitWorktreeOpenMode.Explorer:
                FileExplorerLauncher.OpenInExplorer(path);
                break;
        }
    }

    /// <summary>
    /// 消すワークツリーを（フォルダーとして足してあれば）今のワークスペースから先に外す。言語サーバー・
    /// ファイル監視・ツリーが握ったまま消すと、Windows では「使用中」で削除が失敗する。
    /// プライマリは外せない（そもそも「いま開いているワークツリー」は削除できないようにしてある）。
    /// </summary>
    private void DetachWorktreeFolders(string worktreePath) {
        var inside = _workspace.Folders
            .Where(f => WorkspacePaths.IsWithin(worktreePath, f)
                        && !string.Equals(f, _workspace.PrimaryFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var folder in inside)
            _vm.FolderTree.RemoveFolderFromWorkspace(folder);
    }
}
