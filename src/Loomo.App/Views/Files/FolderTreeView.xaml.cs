using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView : UserControl
{
    private readonly FolderTreeFileOperationSession _fileOperations = new();
    private readonly FolderTreeKeyboardController _keyboardController;
    private readonly FolderTreeSelectionInteractionController _selectionInteraction;

    public FolderTreeView()
    {
        InitializeComponent();
        _keyboardController = new FolderTreeKeyboardController(new FolderTreeKeyboardActions(
            CurrentSelection,
            SelectAllVisibleNodes,
            PasteInto,
            DuplicateNodes,
            undo => { if (undo) UndoFileOperation(); else RedoFileOperation(); },
            OpenSelectedContextMenu,
            ClearMultiSelection,
            MoveVisibleSelection,
            (tree, key) => RaiseKey(tree, key),
            Activate,
            GoToEdge,
            RenameNode,
            DeleteNodes));
        _selectionInteraction = new FolderTreeSelectionInteractionController(
            FileTree, () => DataContext as FolderTreeViewModel, ClearMultiSelection);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is FolderTreeViewModel oldVm)
                oldVm.SelectionRestored -= OnSelectionRestored;
            if (e.NewValue is FolderTreeViewModel newVm)
                newVm.SelectionRestored += OnSelectionRestored;
        };
        Unloaded += (_, _) =>
        {
            _fileOperations.CancelPending();
            _selectionInteraction.StopPendingSelectionPreview();
            _selectionInteraction.StopTypeAheadTimer();
        };
    }

    /// <summary>ツリー本体へキーボードフォーカスを移す。未選択なら先頭ノードを選んでフォーカスする。
    /// TreeView 自体にフォーカスしても j/k・矢印キーの移動は効かない（キーボード移動は TreeViewItem
    /// 側の実装）ので、必ず項目コンテナへ入れる。パネルを開いた直後はコンテナがまだ生成されて
    /// いないことがあるため、その場合はレイアウト確定後にもう一度試す。</summary>
    public void FocusTree()
        => _selectionInteraction.FocusTree();

    // 「フォルダーをワークスペースに追加」ボタン。選んだフォルダーをマルチルートワークスペースへ
    // 追加する（既存フォルダーと同一・祖先/子孫関係のときは ViewModel 側で無視される）。
    private void OnAddFolderToWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "ワークスペースに追加するフォルダーを選択" };
        if (dlg.ShowDialog(OwnerWindow) == true && !vm.AddFolderToWorkspace(dlg.FolderName))
            Services.ToastService.Info(
                $"「{Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/'))}」は追加しませんでした（既にワークスペースに含まれるフォルダーです）。");
    }

    private void OnTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView || e.OriginalSource is not DependencyObject source)
            return;

        // ItemsControl.ContainerFromElement(tree, ...) はトップレベルのコンテナを返すため、
        // 「変更のみ表示」でディレクトリ配下にネストした変更ファイルではディレクトリの
        // コンテナが返り、IsDirectory 判定で弾かれてしまう。クリック位置から最も近い
        // TreeViewItem をビジュアルツリーを遡って取得する。
        var item = WpfTreeTraversal.FindAncestor<TreeViewItem>(source);
        if (item?.DataContext is not FileNodeViewModel node || node.IsDirectory)
            return;

        if (DataContext is FolderTreeViewModel vm)
        {
            vm.NotifyActivated(node.FullPath);
            e.Handled = true;
        }
    }

    // 1 クリック操作：フォルダ行は開閉（クリックした階層だけをトグルし、配下は遅延読込の
    // ままにして完全展開はしない）、ファイル行はプレビュータブで開く（編集するまでタブ確定せず、
    // 次のクリックで中身が差し替わる）。矢印トグル自身のクリックは IsChecked 経由で既にトグル
    // されるため除外する。ダブルクリック（ClickCount=2）は二重トグルになるので無視する。
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source)
            return;

        if (WpfTreeTraversal.FindAncestor<ToggleButton>(source) is not null)
            return;

        // Ctrl/Shift+クリックは複数選択の操作なので、フォルダ開閉・ファイルのプレビュー表示は
        // 起こさない（選択集合の更新だけで済ませる。既に PreviewMouseLeftButtonDown 側で処理済み）。
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            return;

        if (WpfTreeTraversal.FindAncestor<TreeViewItem>(source)?.DataContext is not FileNodeViewModel node)
            return;

        if (node.IsDirectory)
            node.IsExpanded = !node.IsExpanded;
        else if (DataContext is FolderTreeViewModel vm)
        {
            vm.NotifyPreviewRequested(node.FullPath);
            // プレビューでエディタがフォーカスを奪うため、ツリーへ戻して選択操作を続けられるようにする。
            _selectionInteraction.RestoreFocusAfterPreview(node);
        }
    }

    /// <summary>キーボード移動で選択が変わったらエディタのプレビュータブを追従させる（単クリックと同じ
    /// 「まだツリーを操作中」の扱い＝プレビューのあともフォーカスはツリーに残す）。マウス操作中の選択変更は
    /// <see cref="OnTreeMouseLeftButtonUp"/> 側が出すので二重に開かない。右クリック（メニューを出すための
    /// 選択）でも開かない。</summary>
    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _selectionInteraction.OnSelectedItemChanged(e.NewValue);

    // 復元された選択は「見せるだけ」：プレビューもフォーカス移動もせず、展開したコンテナの生成・
    // レイアウト確定を待ってから縦方向に見える位置へスクロールする。
    private void OnSelectionRestored(object? sender, FileNodeViewModel node)
        => _selectionInteraction.OnSelectionRestored(node);

    // Vim 風キーボード操作:
    //   j/k 上下移動、h 折りたたみ/親へ、l 展開/ファイルを開く、gg 先頭、G 末尾。
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TreeView tree)
            return;

        // ツリー内のルート切替 ComboBox 等がフォーカスを持つ間は、親 TreeView の
        // PreviewKeyDown で標準の入力・選択操作を奪わない。アドレスバーはツリー外だが、
        // このガードは同じ PreviewKeyDown の経路に追加された子コントロールにも効く。
        if (e.OriginalSource is DependencyObject source
            && (WpfTreeTraversal.FindAncestor<TextBoxBase>(source) is not null
                || WpfTreeTraversal.FindAncestor<ComboBox>(source) is not null
                || WpfTreeTraversal.FindAncestor<PasswordBox>(source) is not null))
            return;

        if (_keyboardController.HandleKeyDown(tree, e, _multiSelection.Count > 0))
            e.Handled = true;
    }

    // ツリーへの直接の文字入力は Explorer の type-ahead 選択として扱う。j/k/g などは
    // KeyDown 側で Vim 操作として処理されるため、ここには通常の文字入力だけが届く。
    private void OnTreePreviewTextInput(object sender, TextCompositionEventArgs e)
        => _selectionInteraction.OnPreviewTextInput(sender, e);

    private void SelectAllVisibleNodes()
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var visible = VisibleNodes(vm.Nodes).ToList();
        if (visible.Count == 0)
            return;

        ClearMultiSelection();
        foreach (var node in visible)
            AddToMultiSelection(node);

        // フォーカスだけツリーに入っている、または折りたたみでネイティブ選択が非表示になっている
        // 状態でも、表示中の現在地を作る。Shift+F10 が常に表示中の項目へ届くようにする。
        if (FileTree.SelectedItem is not FileNodeViewModel current || !visible.Contains(current))
            _selectionInteraction.SelectAndReveal(visible[0], focus: true);
        else
            _selectionInteraction.FindContainer(current)?.Focus();
    }

    private void OpenSelectedContextMenu(TreeView tree)
    {
        if (tree.SelectedItem is not FileNodeViewModel node)
        {
            if (tree.ContextMenu is { } emptyMenu)
                emptyMenu.IsOpen = true;
            return;
        }

        var container = _selectionInteraction.FindContainer(node);
        if (container is null)
            return;

        // キーボード移動直後の遅延プレビューがメニュー表示中にファイルを開いてフォーカスを
        // 奪わないようにする。右クリック経路は Mouse.RightButton の判定で既に抑止される。
        _selectionInteraction.StopPendingSelectionPreview();
        _selectionInteraction.SuppressSelectionPreview(() =>
        {
            container.IsSelected = true;
            container.Focus();
            if (WpfTreeTraversal.FindContextMenuHost(container) is { ContextMenu: { } menu } target)
            {
                menu.PlacementTarget = target;
                menu.IsOpen = true;
            }
        });
    }

    private void Activate(FileNodeViewModel node)
    {
        if (DataContext is FolderTreeViewModel vm)
            vm.NotifyActivated(node.FullPath);
    }

    // ===== ヘルパー =====

    // 展開済みノードを表示順（深さ優先）で列挙する。gg/G の対象範囲。
    private static IEnumerable<FileNodeViewModel> VisibleNodes(IEnumerable<FileNodeViewModel> nodes)
        => FolderTreeKeyboardNavigation.EnumerateVisibleNodes(nodes);

    private void GoToEdge(bool last)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var all = VisibleNodes(vm.Nodes).ToList();
        if (all.Count == 0)
            return;

        _selectionInteraction.SelectAndReveal(last ? all[^1] : all[0], focus: true);
    }

    /// <summary>展開状態を反映した表示順で、現在の選択を一つ前後へ移動する。</summary>
    private void MoveVisibleSelection(TreeView tree, int delta)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var visible = VisibleNodes(vm.Nodes).ToList();
        if (visible.Count == 0)
            return;

        var currentIndex = tree.SelectedItem is FileNodeViewModel current
            ? visible.IndexOf(current)
            : -1;
        var targetIndex = FolderTreeKeyboardNavigation.FindAdjacentIndex(
            visible.Count, currentIndex, delta);
        if (targetIndex >= 0)
            _selectionInteraction.SelectAndReveal(visible[targetIndex], focus: true);
    }

    // 遅延読込ツリーで指定パスを上から順に展開し、たどり着いたノードを選択・表示する。
    // ShellWindow からエディタの現在ファイルをツリーへ同期表示するために使う。
    public void RevealPath(string fullPath)
    {
        if (DataContext is FolderTreeViewModel vm)
            RevealStep(vm.Nodes, fullPath);
    }

    private void RevealStep(IEnumerable<FileNodeViewModel> level, string fullPath)
    {
        var (target, descend) = FilePathRelations.FindPathMatch(
            level, fullPath, node => node.FullPath, node => node.IsDirectory);

        if (target is not null)
        {
            // 同期表示・ドロップ後の表示は「見せるだけ」。プレビューは開かない。
            _selectionInteraction.SuppressSelectionPreview(
                () => _selectionInteraction.SelectAndReveal(target, focus: true));
            return;
        }

        if (descend is null)
            return;

        descend.IsExpanded = true;   // VM 側の子を同期読込
        // 展開したコンテナの生成・レイアウト確定を待ってから次階層へ降りる。
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => RevealStep(descend.Children, fullPath)));
    }

    // TreeViewItem 既定の BringIntoView は、インデントの深い項目を丸ごと見せようと
    // 水平方向にもスクロールしてしまう（展開・選択のたびに右へ流れる）。
    // 既定動作を止め、ヘッダ行が縦方向に見える分だけスクロールする。
    private void OnItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not TreeViewItem item)
            return;

        e.Handled = true;

        // マウスで直接クリックできた項目は既にビューポート内にある。選択時に WPF が
        // 発行する BringIntoView まで処理すると、項目数が多いツリーでクリックのたびに
        // 行が上端／下端へ寄ってしまう。マウス操作中だけ現在位置を保ち、キーボード移動や
        // RevealPath による明示的な表示要求は従来どおり縦スクロールさせる。
        if (Mouse.LeftButton == MouseButtonState.Pressed)
            return;

        if (WpfTreeTraversal.FindDescendant<ScrollViewer>(FileTree) is not { } scrollViewer)
            return;

        // 対象はヘッダ行（Bd）のみ。item 全体だと展開済みの子を含む高さになる。
        var header = item.Template?.FindName("Bd", item) as FrameworkElement ?? item;
        if (!header.IsVisible)
            return;

        var top = header.TransformToVisual(scrollViewer).Transform(default).Y;
        var bottom = top + header.ActualHeight;

        if (top < 0)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + top);
        else if (bottom > scrollViewer.ViewportHeight)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + (bottom - scrollViewer.ViewportHeight));
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
