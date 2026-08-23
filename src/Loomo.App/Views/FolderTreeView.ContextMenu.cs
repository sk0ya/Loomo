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
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    private CancellationTokenSource? _propertiesLoadCts;

    // ===== ファイル操作（コンテキストメニュー／F2・Delete） =====

    // 右クリックした項目を選択しておく（後続の操作対象を直感的にする）。空き領域なら何もしない。
    // 複数選択中に、その集合に含まれる項目を右クリックしたときは集合を保ったまま（一括操作の対象に
    // するため）。集合の外を右クリックしたときは単一選択に戻す（Explorer 等と同じ挙動）。
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestorTreeViewItem(source) is { } item)
        {
            if (item.DataContext is FileNodeViewModel node && !_multiSelected.Contains(node))
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
    {
        var current = sender as DependencyObject;
        while (current is MenuItem item)
            current = item.Parent;

        if (current is ContextMenu cm)
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
        var name = isDirectory
            ? InputDialog.Prompt(OwnerWindow, title, $"{title}名を入力:")
            : NewFileDialog.Prompt(OwnerWindow);
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

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteNodes(CurrentSelection(ContextNode(sender)));

    // 同じフォルダー内へ複製する（貼り付けと同じ「 - コピー」規則で一意化）。複数選択ならまとめて。
    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateNodes(CurrentSelection(ContextNode(sender)));

    private void DuplicateNodes(IReadOnlyList<FileNodeViewModel> nodes)
    {
        if (nodes.Count == 0 || DataContext is not FolderTreeViewModel vm)
            return;

        string? lastCreated = null;
        // 複数選択ぶんは 1 回の Undo でまとめて戻す。
        using (vm.BeginFileOperationBatch())
            foreach (var node in nodes)
            {
                try { lastCreated = vm.DuplicateEntry(node) ?? lastCreated; }
                catch (InvalidOperationException ex) { ShowError(ex.Message); }
            }

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
        if (nodes.Count == 0 || DataContext is not FolderTreeViewModel vm)
            return;

        var message = nodes.Count == 1
            ? $"{(nodes[0].IsDirectory ? "フォルダー" : "ファイル")}「{nodes[0].Name}」をゴミ箱へ移動しますか？"
            : $"選択した {nodes.Count} 件をゴミ箱へ移動しますか？";
        var confirm = MessageBox.Show(message, "削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        ClearMultiSelection();
        using (vm.BeginFileOperationBatch())
            foreach (var node in nodes)
            {
                try { vm.DeleteEntry(node); }
                catch (InvalidOperationException ex) { ShowError(ex.Message); }
            }
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
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        ExecuteShellAction(vm, ShellFileAction.Open,
            CurrentSelection(ContextNode(sender)).Where(n => !n.IsDirectory));
    }

    private void OnOpenWithAppClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FolderTreeViewModel vm)
            ExecuteShellAction(vm, ShellFileAction.OpenWith, CurrentSelection(ContextNode(sender)));
    }

    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FolderTreeViewModel vm)
            ExecuteShellAction(vm, ShellFileAction.Share, CurrentSelection(ContextNode(sender)));
    }

    private void OnSendToClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FolderTreeViewModel vm)
            ExecuteShellAction(vm, ShellFileAction.SendTo, CurrentSelection(ContextNode(sender)));
    }

    private void ExecuteShellAction(
        FolderTreeViewModel vm,
        ShellFileAction action,
        IEnumerable<FileNodeViewModel> nodes)
    {
        var paths = nodes.Select(n => n.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        var result = vm.ShellOperations.Execute(action, paths);
        if (!result.IsCancelled && result.FailedPaths.Count > 0)
            ShowError(result.ErrorMessage ?? "Shell 操作を実行できませんでした。");
    }

    private void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is not { } node)
            return;

        try
        {
            // ファイルは選択状態で、ディレクトリはその中を開く。
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (File.Exists(node.FullPath))
                info.ArgumentList.Add("/select," + Path.GetFullPath(node.FullPath));
            else if (Directory.Exists(node.FullPath))
                info.ArgumentList.Add(Path.GetFullPath(node.FullPath));
            else
                return;
            Process.Start(info);
        }
        catch
        {
            // explorer 起動失敗は無視。
        }
    }

    /// <summary>選択中の項目をまとめてプロパティウィンドウへ渡す。右クリックした項目が複数選択の
    /// 集合内なら集合を維持し、集合外なら既存の Explorer 同様にその項目だけを対象にする。</summary>
    private async void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        var selected = CurrentSelection(ContextNode(sender));
        if (selected.Count == 0 || _propertiesLoadCts is not null || DataContext is not FolderTreeViewModel vm)
            return;

        var targets = selected
            .Select(node => new FilePropertiesTarget(node.FullPath, node.IsDirectory))
            .ToArray();

        using var cts = new CancellationTokenSource();
        _propertiesLoadCts = cts;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // フォルダーのサイズ計算やネットワーク／長い UNC パスの ACL 読み取りで UI を固めない。
            var result = await Task.Run(
                () => vm.FileProperties.ReadMany(targets, cts.Token),
                cts.Token);
            if (cts.IsCancellationRequested || !IsLoaded)
                return;
            var dialog = new FilePropertiesWindow(result) { Owner = OwnerWindow };
            dialog.ShowDialog();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // ビューがアンロードされた、または読み取りがキャンセルされた場合は何もしない。
        }
        catch (Exception ex)
        {
            ShowError($"プロパティを表示できませんでした: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_propertiesLoadCts, cts))
            {
                _propertiesLoadCts = null;
                Mouse.OverrideCursor = null;
            }
        }
    }

    private async void OnCompressToZipClick(object sender, RoutedEventArgs e)
    {
        if (_zipOperationCts is not null || DataContext is not FolderTreeViewModel vm)
            return;

        var nodes = CurrentSelection(ContextNode(sender));
        if (nodes.Count == 0)
            return;

        using var cts = new CancellationTokenSource();
        _zipOperationCts = cts;
        try
        {
            var archive = await vm.CompressEntriesAsync(nodes, cts.Token);
            ClearMultiSelection();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(archive)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // ビューがアンロードされた場合は、作成途中の一時 ZIP を残さず静かに終了する。
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_zipOperationCts, cts))
                _zipOperationCts = null;
        }
    }

    private void CancelPropertiesLoad() => _propertiesLoadCts?.Cancel();

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
    // ファイルを2つ選んでいれば左＝先・右＝後で突き合わせる。順序は _multiSelected の並び＝
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
        SetFileSystemOperationVisibility(cm, node is null || !node.IsShellItem);
        var ready = DataContext is FolderTreeViewModel vm && vm.IsAiReady;
        var show = ready && node is { IsDirectory: false } && File.Exists(node.FullPath);

        foreach (var item in cm.Items)
            if (item is FrameworkElement { Tag: "AiMenu" } element)
                element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // 「AI」サブメニューを出すときだけ、入力ありワークフローの一覧を流し込む。
        if (show && DataContext is FolderTreeViewModel treeVm)
            PopulateWorkflowMenu(cm, treeVm, node!);

        // 「選択した2つを Diff で比較」は、ファイルをちょうど2つ選んでいるときだけ出す
        // （それ以外では何を左右に置くか決まらない）。
        var twoFiles = SelectedFilesForCompare(node).Count == 2;
        var diffMenu = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "DiffMenu");
        var compareTwo = diffMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Tag as string) == "CompareTwo");
        if (compareTwo is not null)
            compareTwo.Visibility = twoFiles ? Visibility.Visible : Visibility.Collapsed;

        // 複数フォルダーワークスペースの見出し（ワークスペースフォルダー自身）だけ、
        // そのフォルダー内のピン留め切替候補を流し込む。
        if (node is { IsWorkspaceFolderRoot: true } headerNode && DataContext is FolderTreeViewModel vm2)
            PopulateRootSwitchMenu(cm, vm2, headerNode);

        UpdateHistoryMenuItems(cm);

        // 区切り線の整形は、上の出し分けをすべて終えた最後に行う（グループが丸ごと隠れたときに
        // 区切り線だけが残らないようにする）。
        NormalizeSeparators(cm);
        foreach (var submenu in cm.Items.OfType<MenuItem>())
            NormalizeSeparators(submenu);
    }

    /// <summary>グループ分けの区切り線を、実際に見えている項目に合わせて出し分ける。
    /// このメニューは項目の多くが条件付き表示（ファイル／フォルダ、Git 配下、ピン留め済み…）なので、
    /// XAML に区切り線を静的に置くと「区切り線だけが2本続く」「先頭・末尾に区切り線が出る」といった
    /// 見え方になる（WPF は Separator の表示可否を自動調整しない）。前に可視項目があり、かつ後ろにも
    /// 可視項目が続く区切り線だけを残す。</summary>
    internal static void NormalizeSeparators(ItemsControl menu)
    {
        Separator? pending = null;
        var sawVisibleItem = false;

        foreach (var item in menu.Items)
        {
            if (item is Separator separator)
            {
                // 後ろに可視項目が現れたときだけ出す（先頭・連続・末尾の区切り線はこれで消える）。
                separator.Visibility = Visibility.Collapsed;
                pending = sawVisibleItem ? separator : null;
                continue;
            }

            if (item is not FrameworkElement { Visibility: Visibility.Visible })
                continue;

            sawVisibleItem = true;
            if (pending is not null)
            {
                pending.Visibility = Visibility.Visible;
                pending = null;
            }
        }
    }

    // 見出しの「ピン留めフォルダーへ切替」サブメニューを、そのフォルダー自身の切替候補
    // （フォルダー自身＋ピン留めしたサブフォルダー）で作り直す。現在の表示先にはチェックを付ける。
    private void PopulateRootSwitchMenu(ContextMenu cm, FolderTreeViewModel vm, FileNodeViewModel headerNode)
    {
        var switchMenu = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "RootSwitchMenu");
        if (switchMenu is null)
            return;

        var options = vm.RootOptionsFor(headerNode);
        var selected = vm.SelectedRootOptionFor(headerNode);

        switchMenu.Items.Clear();
        foreach (var option in options)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = ReferenceEquals(option, selected),
            };
            item.Click += (_, _) => vm.SwitchRootOption(headerNode, option);
            switchMenu.Items.Add(item);
        }
    }

    // 「AI」→「ワークフロー」サブメニューを、入力ありワークフロー一覧で作り直す。
    // 候補が無ければ隠す（区切り線は NormalizeSeparators が追随する）。
    // 各項目クリックで当該ノードのパスを {{input}} に実行を要求する。
    private void PopulateWorkflowMenu(ContextMenu cm, FolderTreeViewModel vm, FileNodeViewModel node)
    {
        var aiMenu = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "AiMenu");
        if (aiMenu is null)
            return;

        var submenu = aiMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "AiWorkflowMenu");
        if (submenu is null)
            return;

        var workflows = vm.InputWorkflows();
        submenu.Visibility = workflows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
        => CopyLines(CurrentSelection(ContextNode(sender)).Select(n => n.FullPath));

    private void OnCopyRelativePathClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FolderTreeViewModel vm)
            CopyLines(CurrentSelection(ContextNode(sender)).Select(vm.RelativePathFor));
    }

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
        => CopyLines(CurrentSelection(ContextNode(sender)).Select(n => n.Name));

    private static void CopyLines(IEnumerable<string> values) => FileClipboard.CopyLines(values);

    // ===== コピー／切り取り／貼り付け =====
    // 受け渡しの規則（ファイルドロップリスト・Preferred DropEffect）はファイル一覧ペインと共有する
    // FileClipboard が持つ。ここは「何を選んでいるか」だけを決める。

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => FileClipboard.SetFiles(CurrentSelection(ContextNode(sender)).Where(n => !n.IsShellItem).Select(n => n.FullPath), move: false);

    private void OnCutClick(object sender, RoutedEventArgs e)
        => FileClipboard.SetFiles(CurrentSelection(ContextNode(sender)).Where(n => !n.IsShellItem).Select(n => n.FullPath), move: true);

    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteInto(ContextNode(sender));

    private void PasteInto(FileNodeViewModel? contextNode)
    {
        if (DataContext is not FolderTreeViewModel vm || !FileClipboard.ContainsFiles())
            return;

        var targetDir = vm.GetTargetDirectory(contextNode);
        if (targetDir is null)
            return;

        var move = FileClipboard.PrefersMove();
        string? lastPasted = null;
        FileConflictDecision? applyToAll = null;
        var cancelled = false;

        FileConflictDecision ResolveConflict(FileConflictContext context)
        {
            if (applyToAll is { } remembered)
                return remembered;

            var decision = FileConflictDialog.Show(OwnerWindow, context);
            // 名前変更は項目ごとに名前が必要なので、全件適用は上書き／スキップだけにする。
            if (decision.ApplyToAll && decision.Action is (FileConflictAction.Overwrite or FileConflictAction.Skip))
                applyToAll = decision with { ApplyToAll = false };
            return decision;
        }

        try
        {
            using (vm.BeginFileOperationBatch())
                foreach (var source in FileClipboard.GetFiles())
                {
                    var result = vm.PasteEntry(targetDir, source, move, ResolveConflict);
                    if (result.DestinationPath is { } pasted)
                        lastPasted = pasted;
                    if (result.Cancelled)
                    {
                        cancelled = true;
                        break;
                    }
                }
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        // 切り取り→貼り付け（移動）はエクスプローラー同様、成功後にクリップボードを空にする。
        if (move && !cancelled)
            FileClipboard.Clear();

        if (lastPasted is not null)
        {
            var reveal = lastPasted;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(reveal)));
        }
    }

    private void OnAddToGitignoreClick(object sender, RoutedEventArgs e)
    {
        if (ContextNode(sender) is not { } node || DataContext is not FolderTreeViewModel vm)
            return;

        try
        {
            vm.AddToGitignore(node);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
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
        if (DataContext is not FolderTreeViewModel vm)
            return;
        if (undo ? !vm.History.CanUndo : !vm.History.CanRedo)
            return;   // 履歴が空のときは黙って何もしない（エディタ等へキーは渡さない）。

        FileOperationResult result;
        try
        {
            result = undo
                ? vm.UndoFileOperation()
                : await vm.RedoFileOperationAsync();
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        // ツリーの外（ワークスペース外・別ルート）へ効くこともあるので、何が起きたかは必ず言葉で出す。
        ToastService.Info($"{(undo ? "元に戻しました" : "やり直しました")}: {result.Description}");
        if (result.RevealPath is { } reveal)
        {
            ClearMultiSelection();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(reveal)));
        }
    }

    // 「元に戻す」「やり直す」の見出しを次の一手に合わせ、無いときは項目ごと隠す
    // （区切り線は呼び出し側の NormalizeSeparators が追随する）。
    private void UpdateHistoryMenuItems(ContextMenu menu)
    {
        var history = (DataContext as FolderTreeViewModel)?.History;
        foreach (var item in menu.Items.OfType<MenuItem>())
            switch (item.Tag as string)
            {
                case "UndoItem": ApplyHistoryHeader(item, "元に戻す", history?.UndoDescription); break;
                case "RedoItem": ApplyHistoryHeader(item, "やり直す", history?.RedoDescription); break;
            }
    }

    private static void ApplyHistoryHeader(MenuItem item, string verb, string? description)
    {
        item.Visibility = description is null ? Visibility.Collapsed : Visibility.Visible;
        item.Header = description is null ? verb : $"{verb}（{description}）";
    }

    // ツリー空き領域のメニュー。項目の出し分けは Undo/Redo だけなので、それを更新して線をならす。
    private void OnTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        var allowed = DataContext is FolderTreeViewModel vm
            && !FolderTreeShellNamespaces.IsShellPath(vm.CurrentRoot);
        SetFileSystemOperationVisibility(menu, allowed);
        UpdateHistoryMenuItems(menu);
        NormalizeSeparators(menu);
    }

    private static void SetFileSystemOperationVisibility(ContextMenu menu, bool allowed)
    {
        foreach (var item in menu.Items.OfType<FrameworkElement>())
            if (item.Tag as string == "FileSystemOnly")
                item.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowError(string message)
        => ToastService.Error(message);
}
