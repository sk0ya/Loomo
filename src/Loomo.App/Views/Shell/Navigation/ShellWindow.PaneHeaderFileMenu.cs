using System.IO;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタペインの見出し（見ているファイルのパス）の右クリックに足す、ファイルの操作。
/// 項目の名前と入れ子（「パスをコピー」「ペインへ送る」「Git」）はフォルダーツリーの右クリックと揃える。
/// 操作はツリー・ファイル一覧と同じ経路（<see cref="FolderTreeViewModel"/> のパス指定版）へ流し、挙動を分けない。
/// 実在しないファイルでは「名前の変更」「削除」「Git」を出さない。</summary>
public partial class ShellWindow {
    private void AddEditorFileMenuItems(ContextMenu menu, TabEntryViewModel tab) {
        if (!tab.HasRealFile || tab.FilePath is not { Length: > 0 } path)
            return;
        var exists = File.Exists(path);

        menu.Items.Add(new Separator());
        AddEditorFileItem(menu, "エクスプローラーで表示", () => _vm.Tabs.RevealInExplorerCommand.Execute(tab));

        var send = AddEditorFileSubmenu(menu, "ペインへ送る");
        AddEditorFileItem(send, "ターミナル", () => _vm.FolderTree.RequestSetPathInTerminal(path, isDirectory: false),
            enabled: exists);
        AddEditorFileItem(send, "ファイル一覧", () => _vm.FolderTree.RequestRevealPathInFilesPane(path, isDirectory: false),
            enabled: exists);

        menu.Items.Add(new Separator());
        var copy = AddEditorFileSubmenu(menu, "パスをコピー");
        AddEditorFileItem(copy, "フルパス", () => ClipboardText.Set(path));
        AddEditorFileItem(copy, "相対パス", () => ClipboardText.Set(_workspace.ToDisplayPath(path)),
            enabled: _workspace.Contains(path));
        AddEditorFileItem(copy, "名前", () => ClipboardText.Set(Path.GetFileName(path)));

        if (!exists)
            return;
        menu.Items.Add(new Separator());
        AddEditorFileItem(menu, "名前の変更", () => RenameEditorFile(path));
        AddEditorFileItem(menu, "削除", () => DeleteEditorFile(path));
        if (_vm.FolderTree.CanGitForPath(path)) {
            var git = AddEditorFileSubmenu(menu, "Git");
            AddEditorFileItem(git, "履歴を表示", () => _vm.FolderTree.RequestGitHistory(path, isDirectory: false));
            AddEditorFileItem(git, "Git Blame", () => _vm.FolderTree.RequestGitBlame(path));
        }
    }

    private static void AddEditorFileItem(ItemsControl parent, string header, Action run, bool enabled = true) {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        parent.Items.Add(item);
    }

    private static MenuItem AddEditorFileSubmenu(ItemsControl parent, string header) {
        var item = new MenuItem { Header = header };
        parent.Items.Add(item);
        return item;
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
        if (LoomoMessageBox.Show(this, message, "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;
        try { _vm.FolderTree.DeleteEntryAtPath(path, isDirectory: false); }
        catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
    }
}
