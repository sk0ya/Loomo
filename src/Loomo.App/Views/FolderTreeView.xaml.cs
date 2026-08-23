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
    // gg（先頭へ）の 1 つ目の g を受け取った状態。
    private bool _pendingG;

    // Explorer と同じ type-ahead 選択。キー入力が途切れたら次の入力を新しい検索にする。
    private string _typeAheadText = string.Empty;
    private readonly DispatcherTimer _typeAheadResetTimer =
        new() { Interval = TimeSpan.FromMilliseconds(800) };

    // キーボード移動（j/k・矢印・gg/G）で選択が変わったときのプレビュー。押しっぱなしのキーリピートで
    // 通り過ぎる行まで読み込むと重いので、少し落ち着いてから「そのとき選択されている行」を開く。
    private readonly DispatcherTimer _selectionPreviewTimer =
        new() { Interval = TimeSpan.FromMilliseconds(120) };

    // プログラムからの選択（エディタの現在ファイル同期・ドロップ後の表示）では自動プレビューしない。
    private bool _suppressSelectionPreview;
    private CancellationTokenSource? _zipOperationCts;

    public FolderTreeView()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            CancelPropertiesLoad();
            _zipOperationCts?.Cancel();
        };
        _selectionPreviewTimer.Tick += (_, _) =>
        {
            _selectionPreviewTimer.Stop();
            PreviewSelectedNode();
        };
        _typeAheadResetTimer.Tick += (_, _) =>
        {
            _typeAheadResetTimer.Stop();
            _typeAheadText = string.Empty;
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.L
            || (e.KeyboardDevice.Modifiers & ModifierKeys.Control) == 0
            || (e.KeyboardDevice.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        AddressComboBox.Focus();
        AddressComboBox.ApplyTemplate();
        if (AddressComboBox.Template.FindName("PART_EditableTextBox", AddressComboBox) is TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
        else
        {
            AddressComboBox.IsDropDownOpen = true;
        }

        e.Handled = true;
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not FolderTreeViewModel vm)
            return;

        if (vm.NavigateAddress(AddressComboBox.Text))
            AddressComboBox.IsDropDownOpen = false;
        e.Handled = true;
    }

    private void OnAddressSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AddressComboBox.SelectedItem is not string path)
            return;

        AddressComboBox.Text = path;
        AddressComboBox.ApplyTemplate();
        if (AddressComboBox.Template.FindName("PART_EditableTextBox", AddressComboBox) is TextBox textBox)
        {
            textBox.SelectionStart = path.Length;
            textBox.SelectionLength = 0;
        }
    }

    /// <summary>ツリー本体へキーボードフォーカスを移す。未選択なら先頭ノードを選んでフォーカスする。
    /// TreeView 自体にフォーカスしても j/k・矢印キーの移動は効かない（キーボード移動は TreeViewItem
    /// 側の実装）ので、必ず項目コンテナへ入れる。パネルを開いた直後はコンテナがまだ生成されて
    /// いないことがあるため、その場合はレイアウト確定後にもう一度試す。</summary>
    public void FocusTree()
    {
        if (FocusCurrentItem())
            return;

        FileTree.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusCurrentItem()));
    }

    // 選択中（無ければ先頭を選んで）の項目コンテナへキーボードフォーカスを入れる。
    // コンテナが未生成などで入れられなければ false。
    private bool FocusCurrentItem()
    {
        if (!FileTree.IsVisible || FileTree.Items.Count == 0)
            return false;

        if (FileTree.SelectedItem is FileNodeViewModel selected)
            return FindContainer(FileTree, selected) is { } container && container.Focus();

        if (FileTree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem first)
            return false;

        first.IsSelected = true;
        return first.Focus();
    }

    /// <summary>プレビュー表示（ActivateEditorTab → PaneSplitView.FocusFocused）でエディタへ
    /// 同期的に奪われたキーボードフォーカスをツリーへ戻す。単クリックのプレビューは「まだツリーを
    /// 操作している」状態なので、続けて j/k や次のクリックで選択を送れるようにする（ダブルクリック／
    /// Enter の明示的な Activate は対象外＝そのままエディタへ移る）。読み込み直しなどが非同期に走って
    /// あとからフォーカスを奪い返すことがあるため、アイドル時にもう一度確認する
    /// （SearchPanelView.RestoreResultTreeFocus と同じ手当て）。</summary>
    private void RestoreTreeFocus(FileNodeViewModel node)
    {
        FocusNode(node);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => FocusNode(node)));
    }

    private void FocusNode(FileNodeViewModel node)
    {
        if (FileTree.IsKeyboardFocusWithin)
            return;

        if (FindContainer(FileTree, node) is { } container)
            container.Focus();
        else
            FileTree.Focus();
    }

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

    // 1 クリック操作：フォルダ行は開閉（クリックした階層だけをトグルし、配下は遅延読込の
    // ままにして完全展開はしない）、ファイル行はプレビュータブで開く（編集するまでタブ確定せず、
    // 次のクリックで中身が差し替わる）。矢印トグル自身のクリックは IsChecked 経由で既にトグル
    // されるため除外する。ダブルクリック（ClickCount=2）は二重トグルになるので無視する。
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<ToggleButton>(source) is not null)
            return;

        // Ctrl/Shift+クリックは複数選択の操作なので、フォルダ開閉・ファイルのプレビュー表示は
        // 起こさない（選択集合の更新だけで済ませる。既に PreviewMouseLeftButtonDown 側で処理済み）。
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            return;

        if (FindAncestorTreeViewItem(source)?.DataContext is not FileNodeViewModel node)
            return;

        if (node.IsDirectory)
            node.IsExpanded = !node.IsExpanded;
        else if (DataContext is FolderTreeViewModel vm)
        {
            vm.NotifyPreviewRequested(node.FullPath);
            // プレビューでエディタがフォーカスを奪うため、ツリーへ戻して選択操作を続けられるようにする。
            RestoreTreeFocus(node);
        }
    }

    /// <summary>キーボード移動で選択が変わったらエディタのプレビュータブを追従させる（単クリックと同じ
    /// 「まだツリーを操作中」の扱い＝プレビューのあともフォーカスはツリーに残す）。マウス操作中の選択変更は
    /// <see cref="OnTreeMouseLeftButtonUp"/> 側が出すので二重に開かない。右クリック（メニューを出すための
    /// 選択）でも開かない。</summary>
    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelectionPreview
            || Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed)
            return;

        // 移動中は据え置き、止まったところの行を開く（Start だけでは再スタートしない）。
        _selectionPreviewTimer.Stop();
        _selectionPreviewTimer.Start();
    }

    private void PreviewSelectedNode()
    {
        if (FileTree.SelectedItem is not FileNodeViewModel { IsDirectory: false } node
            || DataContext is not FolderTreeViewModel vm)
            return;

        var hadFocus = FileTree.IsKeyboardFocusWithin;
        vm.NotifyPreviewRequested(node.FullPath);
        if (hadFocus)
            RestoreTreeFocus(node);
    }

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
            && (FindAncestor<TextBoxBase>(source) is not null
                || FindAncestor<ComboBox>(source) is not null
                || FindAncestor<PasswordBox>(source) is not null))
            return;

        // gg 判定用。g 以外のキーが来たらプレフィックス状態を解除する。
        var wasPendingG = _pendingG;
        _pendingG = false;

        // Ctrl+A/C/X/V/D は全選択／コピー／切り取り／貼り付け／複製、Ctrl+Z/Ctrl+Shift+Z/Ctrl+Y はファイル操作の
        // 元に戻す／やり直す（下の Ctrl 早期 return より前で処理する）。
        if ((e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0
            && (e.KeyboardDevice.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            var node = tree.SelectedItem as FileNodeViewModel;
            switch (e.Key)
            {
                case Key.A:
                    SelectAllVisibleNodes();
                    e.Handled = true;
                    return;
                case Key.C:
                    FileClipboard.SetFiles(CurrentSelection(node).Where(n => !n.IsShellItem).Select(n => n.FullPath), move: false);
                    e.Handled = true;
                    return;
                case Key.X:
                    FileClipboard.SetFiles(CurrentSelection(node).Where(n => !n.IsShellItem).Select(n => n.FullPath), move: true);
                    e.Handled = true;
                    return;
                case Key.V:
                    PasteInto(node);
                    e.Handled = true;
                    return;
                case Key.D:
                    DuplicateNodes(CurrentSelection(node));
                    e.Handled = true;
                    return;
                // エディタの Undo とは別系統。ツリーにフォーカスがある間だけ、ファイル操作
                // （作成・名前の変更・移動・コピー・削除）の履歴を 1 手ずつ戻す／進める。
                case Key.Z:
                    if ((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0)
                        RedoFileOperation();
                    else
                        UndoFileOperation();
                    e.Handled = true;
                    return;
                case Key.Y:
                    RedoFileOperation();
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl/Alt/Win 付きの組み合わせは対象外。上位（ウィンドウ）のショートカットへ通す。
        // Shift は N（前のヒット）や G（末尾）の判定に使うので許容する。
        if ((e.KeyboardDevice.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        if (e.Key == Key.F10 && (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0)
        {
            OpenSelectedContextMenu(tree);
            e.Handled = true;
            return;
        }

        // 素の移動キーは複数選択を解除して単一選択のキーボード移動へ戻す（Explorer 等と同じ）。
        // TreeView が処理する矢印／ページ移動もここで先に解除する。Delete/F2/Escape は現在の複数選択を
        // 活かしたいので対象外。
        if (_multiSelected.Count > 0 && e.Key is
            Key.J or Key.K or Key.H or Key.L or Key.Enter or Key.G
            or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End
            or Key.PageUp or Key.PageDown)
            ClearMultiSelection();

        switch (e.Key)
        {
            case Key.J:
                MoveVisibleSelection(tree, delta: 1);
                e.Handled = true;
                break;

            case Key.K:
                MoveVisibleSelection(tree, delta: -1);
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

            case Key.G:
                if ((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0)
                    GoToEdge(last: true);          // G で末尾へ
                else if (wasPendingG)
                    GoToEdge(last: false);         // gg で先頭へ
                else
                    _pendingG = true;              // 1 つ目の g
                e.Handled = true;
                break;

            case Key.Home:
                GoToEdge(last: false);
                e.Handled = true;
                break;

            case Key.End:
                GoToEdge(last: true);
                e.Handled = true;
                break;

            case Key.F2:
                RenameNode(tree.SelectedItem as FileNodeViewModel);
                e.Handled = true;
                break;

            case Key.Delete:
                DeleteNodes(CurrentSelection(tree.SelectedItem as FileNodeViewModel));
                e.Handled = true;
                break;
        }
    }

    // ツリーへの直接の文字入力は Explorer の type-ahead 選択として扱う。j/k/g などは
    // KeyDown 側で Vim 操作として処理されるため、ここには通常の文字入力だけが届く。
    private void OnTreePreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TreeView tree
            || DataContext is not FolderTreeViewModel vm
            || string.IsNullOrEmpty(e.Text)
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        var visible = VisibleNodes(vm.Nodes).ToList();
        if (visible.Count == 0)
            return;

        _typeAheadResetTimer.Stop();
        _typeAheadText += e.Text;
        var current = tree.SelectedItem as FileNodeViewModel;
        var currentIndex = current is null ? -1 : visible.IndexOf(current);
        var matchIndex = FolderTreeKeyboardNavigation.FindTypeAheadMatch(
            visible.Select(n => n.Name).ToList(), _typeAheadText, currentIndex);

        // 入力が続いて一致しなくなった場合は、最後の文字を新しい検索の先頭として試す。
        if (matchIndex < 0 && _typeAheadText.Length > e.Text.Length)
        {
            _typeAheadText = e.Text;
            matchIndex = FolderTreeKeyboardNavigation.FindTypeAheadMatch(
                visible.Select(n => n.Name).ToList(), _typeAheadText, currentIndex);
        }

        if (matchIndex >= 0)
        {
            ClearMultiSelection();
            SelectAndReveal(visible[matchIndex], focus: true);
        }

        _typeAheadResetTimer.Start();
        e.Handled = true;
    }

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
            SelectAndReveal(visible[0], focus: true);
        else
            FindContainer(FileTree, current)?.Focus();
    }

    private void OpenSelectedContextMenu(TreeView tree)
    {
        if (tree.SelectedItem is not FileNodeViewModel node)
        {
            if (tree.ContextMenu is { } emptyMenu)
                emptyMenu.IsOpen = true;
            return;
        }

        var container = FindContainer(tree, node);
        if (container is null)
            return;

        // キーボード移動直後の遅延プレビューがメニュー表示中にファイルを開いてフォーカスを
        // 奪わないようにする。右クリック経路は Mouse.RightButton の判定で既に抑止される。
        _selectionPreviewTimer.Stop();
        var wasSuppressingPreview = _suppressSelectionPreview;
        _suppressSelectionPreview = true;
        try
        {
            container.IsSelected = true;
            container.Focus();
            if (FindContextMenuTarget(container) is { ContextMenu: { } menu } target)
            {
                menu.PlacementTarget = target;
                menu.IsOpen = true;
            }
        }
        finally
        {
            _suppressSelectionPreview = wasSuppressingPreview;
        }
    }

    private static FrameworkElement? FindContextMenuTarget(DependencyObject root)
    {
        if (root is FrameworkElement element && element.ContextMenu is not null)
            return element;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindContextMenuTarget(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    private void Activate(FileNodeViewModel node)
    {
        if (DataContext is FolderTreeViewModel vm)
            vm.NotifyActivated(node.FullPath);
    }

    // ===== ヘルパー =====

    // 展開済みノードを表示順（深さ優先）で列挙する。gg/G の対象範囲。
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

    private void GoToEdge(bool last)
    {
        if (DataContext is not FolderTreeViewModel vm)
            return;

        var all = VisibleNodes(vm.Nodes).ToList();
        if (all.Count == 0)
            return;

        SelectAndReveal(last ? all[^1] : all[0], focus: true);
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
            SelectAndReveal(visible[targetIndex], focus: true);
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
        FileNodeViewModel? target = null;
        FileNodeViewModel? descend = null;
        foreach (var node in level)
        {
            if (PathEquals(node.FullPath, fullPath)) { target = node; break; }
            if (node.IsDirectory && IsAncestor(node.FullPath, fullPath)) { descend = node; break; }
        }

        if (target is not null)
        {
            // 同期表示・ドロップ後の表示は「見せるだけ」。プレビューは開かない。
            _suppressSelectionPreview = true;
            try { SelectAndReveal(target, focus: true); }
            finally { _suppressSelectionPreview = false; }
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

        if (FindDescendant<ScrollViewer>(FileTree) is not { } scrollViewer)
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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } found)
                return found;
        }

        return null;
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
