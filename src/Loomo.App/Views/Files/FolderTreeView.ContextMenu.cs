using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    // ===== ファイル操作（コンテキストメニュー／F2・Delete） =====

    // 右クリックした項目を選択しておく（後続の操作対象を直感的にする）。空き領域なら何もしない。
    // 複数選択中に、その集合に含まれる項目を右クリックしたときは集合を保ったまま（一括操作の対象に
    // するため）。集合の外を右クリックしたときは単一選択に戻す（Explorer 等と同じ挙動）。
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && WpfTreeTraversal.FindAncestor<TreeViewItem>(source) is { } item)
        {
            if (item.DataContext is FileNodeViewModel node && !_multiSelection.Contains(node))
                ClearMultiSelection();
            item.IsSelected = true;
            item.Focus();
        }
    }

    // メニュー項目が属する ContextMenu の配置対象から操作対象ノードを得る。
    // 項目の上のメニューならそのノード、ツリー空き領域のメニューなら null（＝ルート対象）。
    // 子メニュー項目（「Git」＞「履歴を表示」等）の Parent は親 MenuItem なので、ContextMenu まで遡る
    // ——遡らないと選択中ノード頼みのフォールバックに落ち、親メニューと対象がずれ得る。
    private FileNodeViewModel? ContextNode(object sender)
        => WpfContextMenuDataContext.Resolve(
            sender, () => FileTree.SelectedItem as FileNodeViewModel) as FileNodeViewModel;

    private Window? OwnerWindow => Window.GetWindow(this);

    private void OnNewFileClick(object sender, RoutedEventArgs e) => CreateEntry(ContextNode(sender), isDirectory: false);

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => CreateEntry(ContextNode(sender), isDirectory: true);

    private void CreateEntry(FileNodeViewModel? contextNode, bool isDirectory)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var created = FolderTreeFileCommandController.CreateEntry(vm, contextNode, isDirectory, directory =>
        {
            var title = directory ? "新規フォルダー" : "新規ファイル";
            return directory
                ? InputDialog.Prompt(OwnerWindow, title, $"{title}名を入力:")
                : NewFileDialog.Prompt(OwnerWindow);
        });
        if (created is null)
            return;

        // 作成先の親を展開して項目を表示・選択し、ファイルはエディタでも開く。
        // ツリー再構築の直後はコンテナ未生成なので、レイアウト確定後に行う。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            RevealPath(created);
            if (!isDirectory)
                (DataContext as FolderTreeViewModel)?.NotifyActivated(created);
        }));
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => RenameNode(ContextNode(sender));

    private void RenameNode(FileNodeViewModel? node)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var newPath = FolderTreeFileCommandController.RenameEntry(vm, node, entry => InputDialog.Prompt(
            OwnerWindow, "名前の変更", "新しい名前を入力:", entry.Name, selectNameOnly: !entry.IsDirectory));
        if (newPath is null)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(newPath)));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteNodes(CurrentSelection(ContextNode(sender)));

    // 同じフォルダー内へ複製する（貼り付けと同じ「 - コピー」規則で一意化）。複数選択ならまとめて。
    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateNodes(CurrentSelection(ContextNode(sender)));

    private void DuplicateNodes(IReadOnlyList<FileNodeViewModel> nodes)
    {
        var lastCreated = FolderTreeFileCommandController.DuplicateEntries(
            DataContext as FolderTreeViewModel, nodes);
        if (lastCreated is not null)
        {
            var reveal = lastCreated;
            ClearMultiSelection();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(reveal)));
        }
    }

    /// <summary>1件または複数件（<see cref="CurrentSelection"/>）をまとめてゴミ箱へ送る。
    /// 確認は1回だけ（複数件のときは件数をまとめて表示）。</summary>
    private void DeleteNodes(IReadOnlyList<FileNodeViewModel> nodes)
    {
        FolderTreeFileCommandController.DeleteEntries(
            DataContext as FolderTreeViewModel,
            nodes,
            message => MessageBox.Show(message, "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK,
            ClearMultiSelection);
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: false } node
            && DataContext is FolderTreeViewModel vm)
            vm.RequestOpenInBrowser(node.FullPath);
    }

    // 拡張子に紐づく既定のアプリで開く（PDF・画像・Office 等、エディタペインで扱えない素材の逃げ道）。
    // 関連付けが無ければ Windows が「プログラムから開く」を出す。フォルダは「エクスプローラーで表示」と
    // 同じになるので出さない。
    private void OnOpenWithDefaultAppClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.Open, ContextNode(sender), filesOnly: true);

    private void OnOpenWithAppClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.OpenWith, ContextNode(sender));

    private void OnShareClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.Share, ContextNode(sender));

    private void OnSendToClick(object sender, RoutedEventArgs e)
        => ExecuteShellAction(ShellFileAction.SendTo, ContextNode(sender));

    private void ExecuteShellAction(
        ShellFileAction action,
        FileNodeViewModel? contextNode,
        bool filesOnly = false)
    {
        FolderTreeFileCommandController.ExecuteShellAction(
            DataContext as FolderTreeViewModel, action, CurrentSelection(contextNode), filesOnly);
    }

    private void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is not { } node)
            return;
        FileExplorerLauncher.RevealInExplorer(node.FullPath);
    }

    /// <summary>選択中の項目をまとめてプロパティウィンドウへ渡す。右クリックした項目が複数選択の
    /// 集合内なら集合を維持し、集合外なら既存の Explorer 同様にその項目だけを対象にする。</summary>
    private async void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        var selected = CurrentSelection(ContextNode(sender));
        if (DataContext is not FolderTreeViewModel vm)
            return;

        await FileContextMenuPresenter.ShowFolderTreePropertiesAsync(
            this, OwnerWindow, vm, selected, _fileOperations);
    }

    private async void OnCompressToZipClick(object sender, RoutedEventArgs e)
    {
        if (_fileOperations.IsCompressing || DataContext is not FolderTreeViewModel vm)
            return;

        var nodes = CurrentSelection(ContextNode(sender));
        if (nodes.Count == 0)
            return;

        var archive = await _fileOperations.CompressEntriesAsync(vm, nodes);
        if (archive is null)
            return;
        ClearMultiSelection();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(archive)));
    }

    private void OnSetInTerminalClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node && DataContext is FolderTreeViewModel vm)
            vm.RequestSetInTerminal(node);
    }

    private void OnGitBlameClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: false } node && DataContext is FolderTreeViewModel vm)
            vm.RequestGitBlame(node);
    }

    private void OnGitHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node && DataContext is FolderTreeViewModel vm)
            vm.RequestGitHistory(node);
    }

    // Diff ペインへ素材として送る。単体なら「このファイル ↔ クリップボード」、
    // ファイルを2つ選んでいれば左＝先・右＝後で突き合わせる。順序は複数選択 controller の並び＝
    // Ctrl+クリックなら選んだ順、Shift+範囲選択ならツリーの並び順（上が左）。
    private void OnCompareWithClipboardClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: false } node && DataContext is FolderTreeViewModel vm)
            vm.RequestCompare(node.FullPath, rightPath: null);
    }

    private void OnCompareSelectedClick(object sender, RoutedEventArgs e)
    {
        var files = SelectedFilesForCompare(ContextNode(sender));
        if (files.Count == 2 && DataContext is FolderTreeViewModel vm)
            vm.RequestCompare(files[0].FullPath, files[1].FullPath);
    }

    private IReadOnlyList<FileNodeViewModel> SelectedFilesForCompare(FileNodeViewModel? contextNode)
        => CurrentSelection(contextNode).Where(n => !n.IsDirectory).ToList();

    private void OnRevealInFilesPaneClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node && DataContext is FolderTreeViewModel vm)
            vm.RequestRevealInFilesPane(node);
    }

    private void OnSearchInFolderClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: true } node && DataContext is FolderTreeViewModel vm)
            vm.RequestSearchInFolder(node);
    }

    // ツールバーの「すべて展開／すべて折りたたみ」はツリー全体が対象なので、1つの枝だけを
    // 開閉したいときの入口をここに置く。
    private void OnExpandSubtreeClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: true } node && DataContext is FolderTreeViewModel vm)
            vm.ExpandSubtree(node);
    }

    private void OnCollapseSubtreeClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: true } node && DataContext is FolderTreeViewModel vm)
            vm.CollapseSubtree(node);
    }

    // ツリー空き領域のメニュー用（対象はツリー全体）。
    private void OnExpandAllClick(object sender, RoutedEventArgs e)
        => (DataContext as FolderTreeViewModel)?.ExpandAllCommand.Execute(null);

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
        => (DataContext as FolderTreeViewModel)?.CollapseAllCommand.Execute(null);

    private void OnRefreshClick(object sender, RoutedEventArgs e)
        => (DataContext as FolderTreeViewModel)?.RefreshCommand.Execute(null);

    // ノードのコンテキストメニューを開くたびに、条件付き項目（AI・2ファイル比較・ピン留め切替）の
    // 表示可否と中身を決め、最後に区切り線を実際の見え方へ合わせる。
    // 「AI」サブメニューは、AIの暖機が完了（モデルロード済み）していて対象が実在ファイルのときだけ出す。
    private void OnNodeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm)
            return;

        var node = (cm.PlacementTarget as FrameworkElement)?.DataContext as FileNodeViewModel;
        FileContextMenuPresenter.PrepareFolderTreeMenu(
            cm,
            DataContext as FolderTreeViewModel,
            node,
            CurrentSelection(node));
    }

    /// <summary>グループ分けの区切り線を、実際に見えている項目に合わせて出し分ける。
    /// このメニューは項目の多くが条件付き表示（ファイル／フォルダ、Git 配下、ピン留め済み…）なので、
    /// XAML に区切り線を静的に置くと「区切り線だけが2本続く」「先頭・末尾に区切り線が出る」といった
    /// 見え方になる（WPF は Separator の表示可否を自動調整しない）。前に可視項目があり、かつ後ろにも
    /// 可視項目が続く区切り線だけを残す。</summary>
    private void OnTypoCheckClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsDirectory: false } node && DataContext is FolderTreeViewModel vm)
            vm.RequestTypoCheck(node);
    }

    private void OnFileAiClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;
        var tag = (sender as MenuItem)?.Tag as string;
        var action = FileContextMenuPolicy.ResolveFileAiAction(tag);
        if (action is { } selectedAction)
            vm.RequestFileAi(selectedAction, CurrentSelection(ContextNode(sender)));
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

    // ピン留め／解除も UI スレッドでは行わない（照会に加えて反映待ちが入るので、同期に呼ぶと
    // メニューを開いたときより長く固まる）。待っている間はカーソルで進行中を示す。
    private async void OnPinToQuickAccessClick(object sender, RoutedEventArgs e)
        => await SetQuickAccessPinnedAsync(sender, pin: true);

    private async void OnUnpinFromQuickAccessClick(object sender, RoutedEventArgs e)
        => await SetQuickAccessPinnedAsync(sender, pin: false);

    private async Task SetQuickAccessPinnedAsync(object sender, bool pin)
    {
        if (DataContext is FolderTreeViewModel vm)
            await FileContextMenuPresenter.SetFolderTreeQuickAccessPinnedAsync(
                vm, CurrentSelection(ContextNode(sender)), pin);
    }

    private void OnRemoveFromWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { IsWorkspaceFolderRoot: true } node && DataContext is FolderTreeViewModel vm)
            vm.RemoveFromWorkspace(node);
    }

    // ===== パスをコピー（フルパス／相対パス／名前） =====
    // 貼り付け先が何かで欲しい形が変わる：ターミナルや外部アプリにはフルパス、コミットメッセージや
    // AI への指示にはワークスペースからの相対パス、grep や検索欄には名前だけ。3つとも複数選択に対応し、
    // 1行1件で載せる（行区切りならどこへ貼っても壊れない）。
    private void OnCopyPathClick(object sender, RoutedEventArgs e)
        => FolderTreeFileCommandController.CopyPaths(CurrentSelection(ContextNode(sender)));

    private void OnCopyRelativePathClick(object sender, RoutedEventArgs e)
        => FolderTreeFileCommandController.CopyRelativePaths(
            DataContext as FolderTreeViewModel, CurrentSelection(ContextNode(sender)));

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
        => FolderTreeFileCommandController.CopyNames(CurrentSelection(ContextNode(sender)));

    // ===== コピー／切り取り／貼り付け =====
    // 受け渡しの規則（ファイルドロップリスト・Preferred DropEffect）はファイル一覧ペインと共有する
    // FileClipboard が持つ。ここは「何を選んでいるか」だけを決める。

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => FolderTreeFileCommandController.CopyFiles(CurrentSelection(ContextNode(sender)), move: false);

    private void OnCutClick(object sender, RoutedEventArgs e)
        => FolderTreeFileCommandController.CopyFiles(CurrentSelection(ContextNode(sender)), move: true);

    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteInto(ContextNode(sender));

    private void PasteInto(FileNodeViewModel? contextNode)
    {
        var outcome = FolderTreeFileCommandController.PasteFromClipboard(
            DataContext as FolderTreeViewModel,
            contextNode,
            context => FileConflictDialog.Show(OwnerWindow, context));

        if (outcome.Completed && outcome.LastDestinationPath is { } lastPasted)
        {
            var reveal = lastPasted;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(reveal)));
        }
    }

    private void OnAddToGitignoreClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is { } node)
            FolderTreeFileCommandController.AddToGitignore(DataContext as FolderTreeViewModel, node);
    }

    // ===== 元に戻す／やり直す（ファイル操作の Undo/Redo） =====
    // 実体は共有の FileOperationHistory（作成・名前の変更・移動・コピー・削除）。エディタの Undo とは
    // 別物なので、キーはツリーにフォーカスがあるときだけ拾う（PreviewKeyDown → OnTreeKeyDown）。

    private void OnUndoFileOperationClick(object sender, RoutedEventArgs e) => UndoFileOperation();

    private void OnRedoFileOperationClick(object sender, RoutedEventArgs e) => RedoFileOperation();

    internal void UndoFileOperation() => RunHistoryStep(undo: true);

    internal void RedoFileOperation() => RunHistoryStep(undo: false);

    private async void RunHistoryStep(bool undo)
    {
        var result = await FolderTreeFileCommandController.RunHistoryStepAsync(
            DataContext as FolderTreeViewModel, undo);
        if (result is null)
            return;

        if (result.RevealPath is { } reveal)
        {
            ClearMultiSelection();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(reveal)));
        }
    }

    // ツリー空き領域のメニュー。項目の出し分けは Undo/Redo だけなので、それを更新して線をならす。
    private void OnTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        FileContextMenuPresenter.PrepareFolderTreeBackgroundMenu(
            menu, DataContext as FolderTreeViewModel);
    }
}
