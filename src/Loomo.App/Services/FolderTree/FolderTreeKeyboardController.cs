using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal sealed record FolderTreeKeyboardActions(
    Func<FileNodeViewModel?, IReadOnlyList<FileNodeViewModel>> CurrentSelection,
    Action SelectAllVisibleNodes,
    Action<FileNodeViewModel?> PasteInto,
    Action<IReadOnlyList<FileNodeViewModel>> DuplicateNodes,
    Action<bool> RunHistoryStep,
    Action<TreeView> OpenSelectedContextMenu,
    Action ClearMultiSelection,
    Action<TreeView, int> MoveVisibleSelection,
    Action<TreeView, Key> RaiseKey,
    Action<FileNodeViewModel> Activate,
    Action<bool> GoToEdge,
    Action<FileNodeViewModel?> RenameNode,
    Action<IReadOnlyList<FileNodeViewModel>> DeleteNodes);

/// <summary>フォルダーツリーのショートカットと Vim 風移動キーを解釈して View の操作へ渡す。</summary>
internal sealed class FolderTreeKeyboardController(FolderTreeKeyboardActions actions)
{
    private bool _pendingG;

    public bool HandleKeyDown(
        TreeView tree,
        KeyEventArgs e,
        bool hasMultiSelection)
    {
        var modifiers = e.KeyboardDevice.Modifiers;
        var wasPendingG = _pendingG;
        _pendingG = false;

        // Ctrl+A/C/X/V/D とファイル操作履歴。Alt/Windows との組み合わせは上位へ渡す。
        if ((modifiers & ModifierKeys.Control) != 0
            && (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            var node = tree.SelectedItem as FileNodeViewModel;
            switch (e.Key)
            {
                case Key.A:
                    actions.SelectAllVisibleNodes();
                    return true;
                case Key.C:
                    FolderTreeFileCommandController.CopyFiles(actions.CurrentSelection(node), move: false);
                    return true;
                case Key.X:
                    FolderTreeFileCommandController.CopyFiles(actions.CurrentSelection(node), move: true);
                    return true;
                case Key.V:
                    actions.PasteInto(node);
                    return true;
                case Key.D:
                    actions.DuplicateNodes(actions.CurrentSelection(node));
                    return true;
                case Key.Z:
                    actions.RunHistoryStep((modifiers & ModifierKeys.Shift) != 0);
                    return true;
                case Key.Y:
                    actions.RunHistoryStep(false);
                    return true;
            }
        }

        // Ctrl/Alt/Win 付きの組み合わせは対象外。上位（ウィンドウ）のショートカットへ通す。
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return false;

        if (e.Key == Key.F10 && (modifiers & ModifierKeys.Shift) != 0)
        {
            actions.OpenSelectedContextMenu(tree);
            return true;
        }

        // ツリーの移動キーは複数選択を解除し、通常の単一選択へ戻す。
        if (hasMultiSelection && ShouldClearMultiSelection(e.Key))
            actions.ClearMultiSelection();

        switch (e.Key)
        {
            case Key.J:
                actions.MoveVisibleSelection(tree, 1);
                return true;
            case Key.K:
                actions.MoveVisibleSelection(tree, -1);
                return true;
            case Key.H:
                actions.RaiseKey(tree, Key.Left);
                return true;
            case Key.L:
            case Key.Enter:
                if (tree.SelectedItem is FileNodeViewModel { IsDirectory: false } file)
                    actions.Activate(file);
                else
                    actions.RaiseKey(tree, Key.Right);
                return true;
            case Key.G:
                if ((modifiers & ModifierKeys.Shift) != 0)
                    actions.GoToEdge(true);
                else if (wasPendingG)
                    actions.GoToEdge(false);
                else
                    _pendingG = true;
                return true;
            case Key.Home:
                actions.GoToEdge(false);
                return true;
            case Key.End:
                actions.GoToEdge(true);
                return true;
            case Key.F2:
                actions.RenameNode(tree.SelectedItem as FileNodeViewModel);
                return true;
            case Key.Delete:
                actions.DeleteNodes(actions.CurrentSelection(tree.SelectedItem as FileNodeViewModel));
                return true;
            default:
                return false;
        }
    }

    private static bool ShouldClearMultiSelection(Key key)
        => key is Key.J or Key.K or Key.H or Key.L or Key.Enter or Key.G
            or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End
            or Key.PageUp or Key.PageDown;
}
