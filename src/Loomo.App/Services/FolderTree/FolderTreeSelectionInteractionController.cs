using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree の選択プレビュー、検索、復元選択、キーボードフォーカスを調整する。</summary>
internal sealed class FolderTreeSelectionInteractionController
{
    private readonly TreeView _tree;
    private readonly Func<FolderTreeViewModel?> _getViewModel;
    private readonly Action _clearMultiSelection;
    private readonly DispatcherTimer _selectionPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _typeAheadResetTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _suppressSelectionPreview;
    private FileNodeViewModel? _restoredSelection;
    private string _typeAheadText = string.Empty;

    internal FolderTreeSelectionInteractionController(
        TreeView tree,
        Func<FolderTreeViewModel?> getViewModel,
        Action clearMultiSelection)
    {
        _tree = tree;
        _getViewModel = getViewModel;
        _clearMultiSelection = clearMultiSelection;
        _selectionPreviewTimer.Tick += (_, _) =>
        {
            _selectionPreviewTimer.Stop();
            PreviewSelectedNode();
        };
        _typeAheadResetTimer.Tick += (_, _) => ResetTypeAhead();
    }

    /// <summary>選択項目のコンテナへフォーカスし、未生成ならレイアウト後に再試行する。</summary>
    internal void FocusTree()
    {
        if (FocusCurrentItem())
            return;

        _tree.Focus();
        _tree.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusCurrentItem()));
    }

    /// <summary>プレビューが移したフォーカスを戻し、非同期更新後にも再確認する。</summary>
    internal void RestoreFocusAfterPreview(FileNodeViewModel node)
    {
        FocusNode(node);
        _tree.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => FocusNode(node)));
    }

    internal void OnSelectedItemChanged(object? newValue)
    {
        if (_restoredSelection is not null && ReferenceEquals(newValue, _restoredSelection))
        {
            _restoredSelection = null;
            return;
        }

        if (_suppressSelectionPreview
            || Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed)
            return;

        // 移動中は据え置き、止まったところの行を開く。
        _selectionPreviewTimer.Stop();
        _selectionPreviewTimer.Start();
    }

    /// <summary>復元選択はプレビューやフォーカスを動かさず、表示位置だけ合わせる。</summary>
    internal void OnSelectionRestored(FileNodeViewModel node)
    {
        _restoredSelection = node;
        _tree.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ReferenceEquals(_tree.SelectedItem, node))
                _restoredSelection = null;
            if (node.IsSelected)
                FindContainer(node)?.BringIntoView();
        }));
    }

    internal void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!ReferenceEquals(sender, _tree)
            || _getViewModel() is not { } vm
            || string.IsNullOrEmpty(e.Text)
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        var visible = FolderTreeKeyboardNavigation.EnumerateVisibleNodes(vm.Nodes).ToList();
        if (visible.Count == 0)
            return;

        _typeAheadResetTimer.Stop();
        var current = _tree.SelectedItem as FileNodeViewModel;
        var currentIndex = current is null ? -1 : visible.IndexOf(current);
        var search = FolderTreeKeyboardNavigation.ResolveTypeAheadSearch(
            visible, _typeAheadText, e.Text, currentIndex);
        _typeAheadText = search.Input;

        if (search.MatchIndex >= 0)
        {
            _clearMultiSelection();
            SelectAndReveal(visible[search.MatchIndex], focus: true);
        }

        _typeAheadResetTimer.Start();
        e.Handled = true;
    }

    internal void StopPendingSelectionPreview() => _selectionPreviewTimer.Stop();

    internal void StopTypeAheadTimer() => _typeAheadResetTimer.Stop();

    internal void SuppressSelectionPreview(Action action)
    {
        var wasSuppressing = _suppressSelectionPreview;
        _suppressSelectionPreview = true;
        try { action(); }
        finally { _suppressSelectionPreview = wasSuppressing; }
    }

    internal void SelectAndReveal(FileNodeViewModel node, bool focus)
    {
        node.IsSelected = true;
        var container = FindContainer(node);
        if (container is null)
            return;

        container.BringIntoView();
        if (focus)
            container.Focus();
    }

    internal TreeViewItem? FindContainer(FileNodeViewModel target)
        => WpfTreeTraversal.FindItemContainer<TreeViewItem>(_tree,
            container => ReferenceEquals(container.DataContext, target));

    private bool FocusCurrentItem()
    {
        if (!_tree.IsVisible || _tree.Items.Count == 0)
            return false;

        if (_tree.SelectedItem is FileNodeViewModel selected)
            return FindContainer(selected) is { } container && container.Focus();

        if (_tree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem first)
            return false;

        first.IsSelected = true;
        return first.Focus();
    }

    private void FocusNode(FileNodeViewModel node)
    {
        if (_tree.IsKeyboardFocusWithin)
            return;

        if (FindContainer(node) is { } container)
            container.Focus();
        else
            _tree.Focus();
    }

    private void PreviewSelectedNode()
    {
        if (_tree.SelectedItem is not FileNodeViewModel { IsDirectory: false } node
            || _getViewModel() is not { } vm)
            return;

        var hadFocus = _tree.IsKeyboardFocusWithin;
        vm.NotifyPreviewRequested(node.FullPath);
        if (hadFocus)
            RestoreFocusAfterPreview(node);
    }

    private void ResetTypeAhead()
    {
        _typeAheadResetTimer.Stop();
        _typeAheadText = string.Empty;
    }
}
