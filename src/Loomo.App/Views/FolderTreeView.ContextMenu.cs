using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    // ===== ファイル操作（コンテキストメニュー／F2・Delete） =====

    // 右クリックした項目を選択しておく（後続の操作対象を直感的にする）。空き領域なら何もしない。
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestorTreeViewItem(source) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    // メニュー項目が属する ContextMenu の配置対象から操作対象ノードを得る。
    // 項目の上のメニューならそのノード、ツリー空き領域のメニューなら null（＝ルート対象）。
    private FileNodeViewModel? ContextNode(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu cm })
            return cm.PlacementTarget is FrameworkElement { DataContext: FileNodeViewModel node } ? node : null;
        return FileTree.SelectedItem as FileNodeViewModel;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void OnNewFileClick(object sender, RoutedEventArgs e) => CreateEntry(ContextNode(sender), isDirectory: false);

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => CreateEntry(ContextNode(sender), isDirectory: true);

    private void CreateEntry(FileNodeViewModel? contextNode, bool isDirectory)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var parent = vm.GetTargetDirectory(contextNode);
        if (parent is null)
            return;   // フォルダ未選択

        var title = isDirectory ? "新規フォルダー" : "新規ファイル";
        var name = InputDialog.Prompt(OwnerWindow, title, $"{title}名を入力:");
        if (name is null)
            return;

        try
        {
            var created = vm.CreateEntry(parent, name, isDirectory);
            // 作成先の親を展開して項目を表示・選択し、ファイルはエディタでも開く。
            // ツリー再構築の直後はコンテナ未生成なので、レイアウト確定後に行う。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                RevealPath(created);
                if (!isDirectory)
                    (DataContext as FolderTreeViewModel)?.NotifyActivated(created);
            }));
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameNode(ContextNode(sender));

    private void RenameNode(FileNodeViewModel? node)
    {
        if (node is null || DataContext is not FolderTreeViewModel vm)
            return;

        var newName = InputDialog.Prompt(
            OwnerWindow, "名前の変更", "新しい名前を入力:", node.Name, selectNameOnly: !node.IsDirectory);
        if (newName is null)
            return;

        try
        {
            var newPath = vm.RenameEntry(node, newName);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(newPath)));
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteNode(ContextNode(sender));

    private void DeleteNode(FileNodeViewModel? node)
    {
        if (node is null || DataContext is not FolderTreeViewModel vm)
            return;

        var kind = node.IsDirectory ? "フォルダー" : "ファイル";
        var confirm = MessageBox.Show(
            $"{kind}「{node.Name}」をゴミ箱へ移動しますか？",
            "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        try
        {
            vm.DeleteEntry(node);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: false } node
            && DataContext is FolderTreeViewModel vm)
            vm.RequestOpenInBrowser(node.FullPath);
    }

    private void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is not { } node)
            return;

        try
        {
            // ファイルは選択状態で、ディレクトリはその中を開く。
            if (File.Exists(node.FullPath))
                Process.Start("explorer.exe", $"/select,\"{node.FullPath}\"");
            else if (Directory.Exists(node.FullPath))
                Process.Start("explorer.exe", $"\"{node.FullPath}\"");
        }
        catch
        {
            // explorer 起動失敗は無視。
        }
    }

    private void OnSetInTerminalClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node && DataContext is FolderTreeViewModel vm)
            vm.RequestSetInTerminal(node);
    }

    // ノードのコンテキストメニューを開くたびに、末尾の「AI」サブメニュー（と区切り線）の表示可否を決める。
    // AIの暖機が完了（モデルロード済み）していて、対象が実在ファイルのときだけ出す。
    private void OnNodeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm)
            return;

        var node = (cm.PlacementTarget as FrameworkElement)?.DataContext as FileNodeViewModel;
        var ready = DataContext is FolderTreeViewModel vm && vm.IsAiReady;
        var show = ready && node is { IsDirectory: false } && File.Exists(node.FullPath);

        foreach (var item in cm.Items)
            if (item is FrameworkElement { Tag: "AiMenu" } element)
                element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // 「AI」サブメニューを出すときだけ、入力ありワークフローの一覧を流し込む。
        if (show && DataContext is FolderTreeViewModel treeVm)
            PopulateWorkflowMenu(cm, treeVm, node!);
    }

    // 「AI」→「ワークフロー」サブメニューを、入力ありワークフロー一覧で作り直す。
    // 候補が無ければ区切り線ごと隠す。各項目クリックで当該ノードのパスを {{input}} に実行を要求する。
    private void PopulateWorkflowMenu(ContextMenu cm, FolderTreeViewModel vm, FileNodeViewModel node)
    {
        var aiMenu = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "AiMenu");
        if (aiMenu is null)
            return;

        var submenu = aiMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "AiWorkflowMenu");
        var separator = aiMenu.Items.OfType<Separator>().FirstOrDefault(s => (s.Tag as string) == "AiWorkflowSep");
        if (submenu is null)
            return;

        var workflows = vm.InputWorkflows();
        var hasAny = workflows.Count > 0;

        submenu.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        if (separator is not null)
            separator.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;

        submenu.Items.Clear();
        foreach (var wf in workflows)
        {
            var id = wf.Id;
            var item = new MenuItem { Header = wf.Name };
            item.Click += (_, _) => vm.RequestRunWorkflow(node, id);
            submenu.Items.Add(item);
        }
    }

    private void OnTypoCheckClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: false } node && DataContext is FolderTreeViewModel vm)
            vm.RequestTypoCheck(node);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: true } node && DataContext is FolderTreeViewModel vm)
            vm.PinFolder(node.FullPath);
    }

    private void OnUnpinClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: true } node && DataContext is FolderTreeViewModel vm)
            vm.UnpinFolder(node.FullPath);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node)
        {
            try { Clipboard.SetText(node.FullPath); }
            catch { /* クリップボードのロック等は無視 */ }
        }
    }

    private static void ShowError(string message)
        => MessageBox.Show(message, "Loomo", MessageBoxButton.OK, MessageBoxImage.Warning);
}

