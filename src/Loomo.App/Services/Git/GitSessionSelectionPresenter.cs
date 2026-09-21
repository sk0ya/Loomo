using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Gitセッションの選択対象を解決し、関連する表示状態や選択操作へ反映する。</summary>
internal static class GitSessionSelectionPresenter
{
    internal static void SelectReferenceTab(GitSessionViewModel? viewModel, object sender)
    {
        if (viewModel is null || sender is not FrameworkElement { Tag: string tag } button)
            return;

        if (Enum.TryParse<GitReferenceTab>(tag, out var tab))
            viewModel.ReferenceTab = tab;
        (button as ToggleButton)?.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    internal static void SelectRowFromContextMenu(object? originalSource)
    {
        switch (FindRowContainer(originalSource))
        {
            case ListBoxItem listItem:
                listItem.IsSelected = true;
                break;
            case TreeViewItem treeItem:
                treeItem.IsSelected = true;
                break;
        }
    }

    internal static GitBranchInfo? ResolveDoubleClickedBranch(
        object? originalSource, GitBranchInfo? selectedBranch)
        => FindRowContainer(originalSource) is TreeViewItem
            {
                DataContext: BranchTreeNode { Branch: { } clickedBranch }
            }
            ? clickedBranch
            : selectedBranch;

    internal static void OpenCommitDiff(
        GitSessionViewModel? viewModel, System.Collections.IEnumerable selectedItems)
    {
        if (viewModel is not null)
            viewModel.OpenDiffForCommits(GitCommitSelectionMapper.Commits(selectedItems));
    }

    private static DependencyObject? FindRowContainer(object? originalSource)
    {
        var element = originalSource as DependencyObject;
        return (DependencyObject?)WpfTreeTraversal.FindAncestor<ListBoxItem>(element)
            ?? WpfTreeTraversal.FindAncestor<TreeViewItem>(element);
    }
}
