using System.IO;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタペインの見出し（見ているファイルのパス）の右クリックに足す、ファイルの操作。
/// 並びは「コピー」「場所を開く」「ファイル操作」の 3 群。ツリー・ファイル一覧と同じ経路
/// （<see cref="FolderTreeViewModel"/> の node 版と同じ検証・履歴・タブ追従）へ流し、挙動を分けない。
/// 実在しないファイルでは「ファイル操作」群を出さず、Explorer の「表示」だけは最も近い親で動かす。</summary>
public partial class ShellWindow {
    private void AddEditorFileMenuItems(ContextMenu menu, TabEntryViewModel tab) {
        if (!tab.HasRealFile || tab.FilePath is not { Length: > 0 } path)
            return;
        var exists = File.Exists(path);
        var folder = Path.GetDirectoryName(path);
        var folderExists = !string.IsNullOrEmpty(folder) && Directory.Exists(folder);

        menu.Items.Add(new Separator());
        AddEditorFileItem(menu, "パスをコピー", () => _vm.Tabs.CopyPathCommand.Execute(tab));
        AddEditorFileItem(menu, "相対パスをコピー", () => Clipboard.SetText(_workspace.ToDisplayPath(path)),
            enabled: _workspace.Contains(path));

        menu.Items.Add(new Separator());
        AddEditorFileItem(menu, "エクスプローラーで表示", () => _vm.Tabs.RevealInExplorerCommand.Execute(tab));
        AddEditorFileItem(menu, "ファイル一覧で表示", () => _vm.FolderTree.RequestRevealPathInFilesPane(path, isDirectory: false),
            enabled: exists);
        AddEditorFileItem(menu, "このフォルダーをターミナルで開く", () => _vm.FolderTree.RequestSetFolderInTerminal(folder!),
            enabled: folderExists);

        if (!exists)
            return;
        menu.Items.Add(new Separator());
        AddEditorFileItem(menu, "名前を変更…", () => RenameEditorFile(path));
        AddEditorFileItem(menu, "削除…（ゴミ箱へ）", () => DeleteEditorFile(path));
        if (_vm.FolderTree.CanGitForPath(path))
            AddEditorFileItem(menu, "Git の履歴", () => _vm.FolderTree.RequestGitHistory(path, isDirectory: false));
    }

    private static void AddEditorFileItem(ContextMenu menu, string header, Action run, bool enabled = true) {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        menu.Items.Add(item);
    }

    private void RenameEditorFile(string path) {
        var name = Path.GetFileName(path);
        var newName = InputDialog.Prompt(this, "名前の変更", "新しい名前を入力:", name, selectNameOnly: true);
        if (newName is null)
            return;
        try { _vm.FolderTree.RenameEntryAtPath(path, isDirectory: false, newName); }
        catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
    }

    private void DeleteEditorFile(string path) {
        var message = FileContextMenuPolicy.FormatDeleteConfirmation(1, singleIsDirectory: false, Path.GetFileName(path));
        if (MessageBox.Show(this, message, "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;
        try { _vm.FolderTree.DeleteEntryAtPath(path, isDirectory: false); }
        catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
    }
}
