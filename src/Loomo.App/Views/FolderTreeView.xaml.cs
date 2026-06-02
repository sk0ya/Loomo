using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

    // 検索開始時の選択。Esc キャンセルで元の位置へ戻すために退避する。
    private FileNodeViewModel? _selectionBeforeSearch;

    // gg（先頭へ）の 1 つ目の g を受け取った状態。
    private bool _pendingG;

    public FolderTreeView() => InitializeComponent();

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
    {
        var current = source;
        while (current is not null and not TreeViewItem)
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);

        return current as TreeViewItem;
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

    // ===== 検索（/ → 入力 → n/N） =====

    private void OpenSearch()
    {
        _selectionBeforeSearch = FileTree.SelectedItem as FileNodeViewModel;

        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Text = "";
        SearchStatus.Text = "";

        // 直前まで Collapsed だったため、レイアウト確定後でないとフォーカスが移らない。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SearchBox.Focus()));
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;
        RebuildMatches();

        _matchIndex = _matches.Count > 0 ? 0 : -1;
        if (_matchIndex == 0)
            // 入力中はフォーカスを検索ボックスに保ったまま、選択だけ移す。
            SelectAndReveal(_matches[0], focus: false);

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

        if (commit && _matchIndex >= 0 && _matchIndex < _matches.Count)
            SelectAndReveal(_matches[_matchIndex], focus: true);   // 確定：ヒット位置を保つ
        else if (!commit && _selectionBeforeSearch is not null)
            SelectAndReveal(_selectionBeforeSearch, focus: true);  // キャンセル：元の選択へ戻す
        else
            FileTree.Focus();
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
