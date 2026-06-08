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

public partial class FolderTreeView : UserControl
{
    // 直近の検索（/）のクエリと、ヒット集合内の現在位置（n/N で巡回）。
    // ヒット集合はツリー更新で無効化されるため、巡回のたびにクエリから再計算する。
    private string _searchQuery = "";
    private readonly List<FileNodeViewModel> _matches = new();
    private int _matchIndex = -1;

    // 検索開始時の選択（パス）。Esc キャンセルでフィルタを解除し、元の位置へ戻すために退避する。
    // フィルタはツリーを作り直すためインスタンスは無効化される。パスで復元する。
    private string? _selectionBeforeSearchPath;

    // gg（先頭へ）の 1 つ目の g を受け取った状態。
    private bool _pendingG;

    public FolderTreeView()
    {
        InitializeComponent();
        // DataContext はXAML側で後から差し込まれるため、差し替えに追従して購読する。
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FolderTreeViewModel oldVm)
            oldVm.FilterCompleted -= OnFilterCompleted;
        if (e.NewValue is FolderTreeViewModel newVm)
            newVm.FilterCompleted += OnFilterCompleted;
    }

    /// <summary>ツリー本体へキーボードフォーカスを移す。未選択なら先頭ノードを選んでフォーカスする。</summary>
    public void FocusTree()
    {
        if (!EnsureSelection(FileTree))
            FileTree.Focus();
    }

    private void OnTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView || e.OriginalSource is not DependencyObject source)
            return;

        // ItemsControl.ContainerFromElement(tree, ...) はトップレベルのコンテナを返すため、
        // 「変更のみ表示」でディレクトリ配下にネストした変更ファイルではディレクトリの
        // コンテナが返り、IsDirectory 判定で弾かれてしまう。クリック位置から最も近い
        // TreeViewItem をビジュアルツリーを遡って取得する。
        var item = FindAncestorTreeViewItem(source);
        if (item?.DataContext is not FileNodeViewModel node || node.IsDirectory)
            return;

        if (DataContext is FolderTreeViewModel vm)
        {
            vm.NotifyActivated(node.FullPath);
            e.Handled = true;
        }
    }

    private static TreeViewItem? FindAncestorTreeViewItem(DependencyObject source)
        => FindAncestor<TreeViewItem>(source);

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null and not T)
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);

        return current as T;
    }

    // フォルダ行を 1 クリックで開閉する（クリックした階層だけをトグルし、配下は遅延読込の
    // ままにして完全展開はしない）。矢印トグル自身のクリックは IsChecked 経由で既にトグル
    // されるため除外する。ダブルクリック（ClickCount=2）は二重トグルになるので無視する。
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<ToggleButton>(source) is not null)
            return;

        if (FindAncestorTreeViewItem(source)?.DataContext is FileNodeViewModel { IsDirectory: true } node)
            node.IsExpanded = !node.IsExpanded;
    }

    // Vim 風キーボード操作:
    //   j/k 上下移動、h 折りたたみ/親へ、l 展開/ファイルを開く、
    //   / 検索、n/N 次/前のヒット、gg 先頭、G 末尾。
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TreeView tree)
            return;

        // gg 判定用。g 以外のキーが来たらプレフィックス状態を解除する。
        var wasPendingG = _pendingG;
        _pendingG = false;

        // Ctrl/Alt/Win 付きの組み合わせは対象外。上位（ウィンドウ）のショートカットへ通す。
        // Shift は N（前のヒット）や G（末尾）の判定に使うので許容する。
        if ((e.KeyboardDevice.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        switch (e.Key)
        {
            case Key.J:
                // 未選択時は先頭を選ぶだけ（その回は下へ動かさず先頭に留める）。
                if (!EnsureSelection(tree))
                    RaiseKey(tree, Key.Down);
                e.Handled = true;
                break;

            case Key.K:
                if (!EnsureSelection(tree))
                    RaiseKey(tree, Key.Up);
                e.Handled = true;
                break;

            case Key.H:
                // 展開中ディレクトリは折りたたみ、それ以外は親へフォーカス（標準の Left 挙動）。
                RaiseKey(tree, Key.Left);
                e.Handled = true;
                break;

            case Key.L:
            case Key.Enter:
                if (tree.SelectedItem is FileNodeViewModel { IsDirectory: false } file)
                    Activate(file);
                else
                    // 折りたたみ中ディレクトリは展開、展開中なら最初の子へ（標準の Right 挙動）。
                    RaiseKey(tree, Key.Right);
                e.Handled = true;
                break;

            case Key.N:
                // N（Shift+n）は前のヒット、n は次のヒットへ。
                MoveMatch((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1, focusTree: true);
                e.Handled = true;
                break;

            case Key.G:
                if ((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0)
                    GoToEdge(last: true);          // G で末尾へ
                else if (wasPendingG)
                    GoToEdge(last: false);         // gg で先頭へ
                else
                    _pendingG = true;              // 1 つ目の g
                e.Handled = true;
                break;

            case Key.F2:
                RenameNode(tree.SelectedItem as FileNodeViewModel);
                e.Handled = true;
                break;

            case Key.Delete:
                DeleteNode(tree.SelectedItem as FileNodeViewModel);
                e.Handled = true;
                break;

            case Key.Escape:
                // フィルタ適用中（/ を確定した後）なら Esc で解除し、選択中のファイルを全ツリーで再表示する。
                if (DataContext is FolderTreeViewModel vm && !string.IsNullOrEmpty(vm.SearchFilter))
                {
                    var selected = (tree.SelectedItem as FileNodeViewModel)?.FullPath;
                    ClearFilter();
                    if (selected is not null)
                        RevealPath(selected);
                    e.Handled = true;
                }
                break;
        }
    }

    // ツリーへの直接の文字入力（type-ahead 検索）は vim キーと競合するため無効化し、
    // "/" だけはインクリメンタル検索の起動に使う。
    private void OnTreePreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "/")
            OpenSearch();

        e.Handled = true;
    }

    private void Activate(FileNodeViewModel node)
    {
        if (DataContext is FolderTreeViewModel vm)
            vm.NotifyActivated(node.FullPath);
    }

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

    // ===== 検索（/ → 入力 → n/N） =====

    private void OpenSearch()
    {
        _selectionBeforeSearchPath = (FileTree.SelectedItem as FileNodeViewModel)?.FullPath;

        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Text = "";   // TextChanged 経由でフィルタも空に戻る
        SearchStatus.Text = "";

        // 直前まで Collapsed だったため、レイアウト確定後でないとフォーカスが移らない。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SearchBox.Focus()));
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;

        // ツリーのフィルタ（重い列挙＋git）は VM 側でデバウンス＋バックグラウンド実行する。
        // ここでは即時にクエリだけ渡す（ハイライトの更新もこの設定で走る）。
        // 先頭ヒットの選択・件数表示は完了通知（FilterCompleted）で行う。
        if (DataContext is FolderTreeViewModel vm)
            vm.SearchFilter = _searchQuery;

        if (string.IsNullOrEmpty(_searchQuery))
        {
            _matches.Clear();
            _matchIndex = -1;
            UpdateSearchStatus();
        }
        else
        {
            SearchStatus.Text = "検索中…";
        }
    }

    // VM のバックグラウンドフィルタが Nodes へ反映され終わったら、先頭ヒットを選び件数を出す。
    private void OnFilterCompleted(object? sender, EventArgs e)
    {
        // 検索バーが開いている間だけ自動選択する（Enter 確定後などに割り込まない）。
        if (SearchBar.Visibility != Visibility.Visible)
            return;

        RebuildMatches();
        _matchIndex = _matches.Count > 0 ? 0 : -1;
        if (_matchIndex == 0)
        {
            // 入力中はフォーカスを検索ボックスに保ったまま、選択だけ先頭ヒットへ移す。
            // ツリーは直前に作り直されコンテナ未生成なので、レイアウト確定後にスクロールする。
            var first = _matches[0];
            first.IsSelected = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectAndReveal(first, focus: false)));
        }

        UpdateSearchStatus();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CloseSearch(commit: true);
                e.Handled = true;
                break;

            case Key.Escape:
                CloseSearch(commit: false);
                e.Handled = true;
                break;

            case Key.Down:
                MoveMatch(1, focusTree: false);
                e.Handled = true;
                break;

            case Key.Up:
                MoveMatch(-1, focusTree: false);
                e.Handled = true;
                break;
        }
    }

    private void CloseSearch(bool commit)
    {
        SearchBar.Visibility = Visibility.Collapsed;

        if (commit)
        {
            // 確定：フィルタは維持したまま、選択中のヒットへフォーカスを移す。
            // フィルタを残すことで「一致したファイルだけ」を引き続き辿れる（Esc で解除）。
            if (_matchIndex >= 0 && _matchIndex < _matches.Count)
                SelectAndReveal(_matches[_matchIndex], focus: true);
            else
                FileTree.Focus();
        }
        else
        {
            // キャンセル：フィルタを解除して全ツリーへ戻し、元の選択位置を復元する。
            ClearFilter();
            if (_selectionBeforeSearchPath is not null)
                RevealPath(_selectionBeforeSearchPath);
            else
                FileTree.Focus();
        }
    }

    private void ClearFilter()
    {
        _searchQuery = "";
        _matches.Clear();
        _matchIndex = -1;
        if (DataContext is FolderTreeViewModel vm)
            vm.SearchFilter = "";
    }

    private void MoveMatch(int delta, bool focusTree)
    {
        // ツリー更新でヒット集合が作り直されている場合に備え、毎回クエリから再計算する。
        var current = _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex] : null;
        RebuildMatches();

        if (_matches.Count == 0)
        {
            _matchIndex = -1;
            UpdateSearchStatus();
            return;
        }

        // 直前の位置を引き継ぐ。見失った（更新で別インスタンスになった）場合は端から。
        var baseIndex = current is not null ? _matches.IndexOf(current) : -1;
        if (baseIndex < 0)
            baseIndex = delta > 0 ? -1 : 0;

        _matchIndex = ((baseIndex + delta) % _matches.Count + _matches.Count) % _matches.Count;
        SelectAndReveal(_matches[_matchIndex], focus: focusTree);
        UpdateSearchStatus();
    }

    private void RebuildMatches()
    {
        _matches.Clear();
        if (DataContext is not FolderTreeViewModel vm || string.IsNullOrEmpty(_searchQuery))
            return;

        foreach (var node in VisibleNodes(vm.Nodes))
            if (node.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                _matches.Add(node);
    }

    private void UpdateSearchStatus()
    {
        SearchStatus.Text = _matches.Count > 0
            ? $"{_matchIndex + 1}/{_matches.Count}"
            : string.IsNullOrEmpty(_searchQuery) ? "" : "一致なし";
    }

    private void GoToEdge(bool last)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var all = VisibleNodes(vm.Nodes).ToList();
        if (all.Count == 0)
            return;

        SelectAndReveal(last ? all[^1] : all[0], focus: true);
    }

    // ===== ヘルパー =====

    // 展開済みノードを表示順（深さ優先）で列挙する。検索・gg/G の対象範囲。
    private static IEnumerable<FileNodeViewModel> VisibleNodes(IEnumerable<FileNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.IsDirectory && node.IsExpanded)
                foreach (var child in VisibleNodes(node.Children))
                    yield return child;
        }
    }

    // 遅延読込ツリーで指定パスを上から順に展開し、たどり着いたノードを選択・表示する。
    // フィルタ解除後（全ツリーは未展開）に元の選択を復元するために使う。
    private void RevealPath(string fullPath)
    {
        if (DataContext is FolderTreeViewModel vm)
            RevealStep(vm.Nodes, fullPath);
    }

    private void RevealStep(IEnumerable<FileNodeViewModel> level, string fullPath)
    {
        FileNodeViewModel? target = null;
        FileNodeViewModel? descend = null;
        foreach (var node in level)
        {
            if (PathEquals(node.FullPath, fullPath)) { target = node; break; }
            if (node.IsDirectory && IsAncestor(node.FullPath, fullPath)) { descend = node; break; }
        }

        if (target is not null)
        {
            SelectAndReveal(target, focus: true);
            return;
        }

        if (descend is null)
            return;

        descend.IsExpanded = true;   // VM 側の子を同期読込
        // 展開したコンテナの生成・レイアウト確定を待ってから次階層へ降りる。
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => RevealStep(descend.Children, fullPath)));
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAncestor(string directory, string path)
    {
        var dir = Path.GetFullPath(directory).TrimEnd('\\', '/');
        var full = Path.GetFullPath(path);
        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectAndReveal(FileNodeViewModel node, bool focus)
    {
        node.IsSelected = true;

        var container = FindContainer(FileTree, node);
        if (container is null)
            return;

        container.BringIntoView();
        if (focus)
            container.Focus();
    }

    // データ項目に対応する TreeViewItem を、展開済みコンテナを辿って探す。
    private static TreeViewItem? FindContainer(ItemsControl parent, FileNodeViewModel target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct)
            return direct;

        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container
                && FindContainer(container, target) is { } found)
                return found;
        }

        return null;
    }

    // まだ何も選択されていなければ先頭ノードを選択・フォーカスして true を返す。
    // true のとき呼び出し側はその回の移動を行わず、選択を先頭に留める。
    private static bool EnsureSelection(TreeView tree)
    {
        if (tree.SelectedItem is not null || tree.Items.Count == 0)
            return false;

        if (tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
        {
            first.IsSelected = true;
            first.Focus();
            return true;
        }

        return false;
    }

    // 指定キーの KeyDown を再発行し、TreeView/TreeViewItem 標準のキーボード操作へ委譲する。
    private static void RaiseKey(Visual origin, Key key)
    {
        var source = PresentationSource.FromVisual(origin);
        if (source is null)
            return;

        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }
}
