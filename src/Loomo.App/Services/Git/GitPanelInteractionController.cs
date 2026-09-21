using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>Git変更一覧の行選択、差分表示、ツリー展開をまとめて扱う。</summary>
internal sealed class GitPanelInteractionController
{
    private readonly ListBox _stagedList;
    private readonly TreeView _workingTree;
    private readonly Func<GitPanelViewModel?> _getViewModel;

    internal GitPanelInteractionController(
        ListBox stagedList,
        TreeView workingTree,
        Func<GitPanelViewModel?> getViewModel)
    {
        _stagedList = stagedList;
        _workingTree = workingTree;
        _getViewModel = getViewModel;
        _stagedList.SelectionChanged += OnStagedSelectionChanged;
        _stagedList.MouseDoubleClick += OnChangeDoubleClick;
        _stagedList.PreviewMouseRightButtonDown += OnListRightButtonDown;
        _workingTree.MouseDoubleClick += OnChangeDoubleClick;
        _workingTree.PreviewMouseLeftButtonDown += OnTreeLeftButtonDown;
        _workingTree.PreviewMouseRightButtonDown += OnTreeRightButtonDown;
    }

    private void OnStagedSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _getViewModel()?.SetStagedSelection(_stagedList.SelectedItems);

    private void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource) is not { } item)
            return;
        e.Handled = true;
        _getViewModel()?.OpenDiffCommand.Execute(item);
    }

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)
            is { IsSelected: false } item)
        {
            _stagedList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnTreeLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (WpfTreeTraversal.FindAncestor<CheckBox>(source) is not null)
            return;
        if (e.ClickCount == 1
            && WpfTreeTraversal.FindAncestor<TreeViewItem>(source) is { HasItems: true } item)
            item.IsExpanded = !item.IsExpanded;
    }

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)
            is not { } item)
            return;
        item.IsSelected = true;
        item.Focus();
    }

    private static GitChangeItem? FindItem(object source)
    {
        var element = WpfTreeTraversal.FindAncestor<FrameworkElement>(
            source as DependencyObject,
            candidate => candidate.DataContext is GitChangeItem or GitChangeTreeNode { Change: not null });
        return element?.DataContext switch
        {
            GitChangeItem item => item,
            GitChangeTreeNode { Change: { } change } => change,
            _ => null,
        };
    }
}
