using System.Windows;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペース切替ポップアップの名前・フォルダー・パス操作を確認して実行する。</summary>
internal static class WorkspaceSwitcherActionPresenter
{
    public static void RemoveFolder(
        Window? owner,
        WorkspaceListViewModel? vm,
        WorkspaceFolderEntryViewModel folder,
        Action close)
    {
        close();
        var answer = MessageBox.Show(owner,
            $"「{folder.Owner.Label}」からフォルダー {folder.Path} を取り除きますか？\n" +
            "フォルダ自体は削除されません。", "ワークスペースフォルダーの削除",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            vm?.RemoveFolder(folder);
    }

    public static void Rename(
        Window? owner,
        WorkspaceListViewModel? vm,
        WorkspaceEntryViewModel entry,
        Action close)
    {
        if (vm is null)
            return;
        close();
        var name = InputDialog.Prompt(owner, "ワークスペースの表示名",
            $"「{entry.Label}」の表示名を入力してください（空にするとフォルダ名 {entry.Name} に戻ります）",
            entry.HasCustomName ? entry.Label : "", allowEmpty: true);
        if (name is not null)
            vm.Rename(entry, name);
    }

    public static void CopyPath(string path, Action close)
    {
        ClipboardText.Set(path);
        close();
    }

    public static void OpenPath(
        string path,
        bool revealInExplorer,
        Action close,
        Action<string> showError)
    {
        var error = WorkspaceWindowLauncher.TryOpen(path, revealInExplorer);
        if (error is null)
            close();
        else
            showError(error);
    }
}
