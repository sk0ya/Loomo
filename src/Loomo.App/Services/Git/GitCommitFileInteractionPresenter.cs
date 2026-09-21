using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>コミット変更ファイル一覧の選択対象、差分・オープン操作、メニュー可否を反映する。</summary>
internal sealed class GitCommitFileInteractionPresenter
{
    private readonly TreeView _fileList;
    private readonly Func<GitSessionViewModel?> _getViewModel;

    internal GitCommitFileInteractionPresenter(
        TreeView fileList,
        Func<GitSessionViewModel?> getViewModel)
    {
        _fileList = fileList;
        _getViewModel = getViewModel;
    }

    internal CommitFileNode? SelectedFile
        => _fileList.SelectedItem is CommitFileNode { IsDirectory: false } node ? node : null;

    internal void OnDoubleClick(MouseButtonEventArgs e)
    {
        if (_getViewModel() is { } vm && SelectedFile?.NavigatePath is { } path)
        {
            e.Handled = true;
            vm.RequestChangedFileDiffWindow(path);
        }
    }

    internal void OpenDiffWindow()
    {
        if (_getViewModel() is { } vm && SelectedFile?.NavigatePath is { } path)
            vm.RequestChangedFileDiffWindow(path);
    }

    internal async Task OpenFileAsync()
    {
        if (_getViewModel() is { } vm && SelectedFile?.NavigatePath is { } path)
            await vm.OpenChangedFileAsync(path);
    }

    internal void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        var pointerOnRow = WpfTreeTraversal.FindAncestor<TreeViewItem>(
            e.OriginalSource as DependencyObject) is not null;
        e.Handled = !GitSessionMenuPolicyMapper.CanOpenChangedFileMenu(
            keyboardInvocation: e.CursorLeft < 0,
            pointerOnRow: pointerOnRow,
            hasSelectedFile: SelectedFile?.NavigatePath is not null);
    }

    internal void CopyPath()
        => ClipboardText.Set(SelectedFile?.NavigatePath);
}
