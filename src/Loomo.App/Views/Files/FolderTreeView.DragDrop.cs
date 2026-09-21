using System.Collections.Generic;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    private FolderTreeDragDropController? _dragDropController;

    private FolderTreeDragDropController DragDropController
        => _dragDropController ??= new FolderTreeDragDropController(
            FileTree,
            () => DataContext as FolderTreeViewModel,
            node => _multiSelection.Contains(node)
                ? CurrentSelection(node)
                : (IReadOnlyList<FileNodeViewModel>)new[] { node },
            ApplySelectionModifiers,
            RevealPath);

    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragDropController.OnPreviewMouseLeftButtonDown(e);

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
        => DragDropController.OnPreviewMouseMove(e);

    private void OnTreeDragOver(object sender, DragEventArgs e)
        => DragDropController.OnDragOver(e);

    private void OnTreeDrop(object sender, DragEventArgs e)
        => DragDropController.OnDrop(e);
}
