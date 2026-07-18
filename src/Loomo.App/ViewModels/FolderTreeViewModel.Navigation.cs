using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var node in Nodes) ExpandRecursive(node, depth: 0);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes) CollapseRecursive(node);
    }

    private static void ExpandRecursive(FileNodeViewModel node, int depth)
    {
        if (!node.IsDirectory || depth > FolderTreeFilter.MaxDepth || FolderTreeFilter.IsReparsePoint(node.FullPath))
            return;
        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandRecursive(child, depth + 1);
    }

    private static void CollapseRecursive(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        node.IsExpanded = false;
        foreach (var child in node.Children) CollapseRecursive(child);
    }
}
