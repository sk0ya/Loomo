using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Git ペインのキー入力後のフォーカス・選択操作を管理する。</summary>
internal sealed class GitSessionKeyboardController
{
    private readonly FrameworkElement _owner;
    private readonly ListView _logList;
    private readonly TreeView _branchList;
    private readonly TextBox _branchFilterBox;
    private readonly TextBox _logFilterBox;
    private readonly Func<GitSessionViewModel?> _getViewModel;
    private readonly Func<GitBranchInfo?> _getSelectedBranch;
    private readonly Func<GitSessionViewModel, GitBranchInfo, Task> _showBranchLog;
    private bool _pendingLogG;

    internal GitSessionKeyboardController(
        FrameworkElement owner,
        ListView logList,
        TreeView branchList,
        TextBox branchFilterBox,
        TextBox logFilterBox,
        Func<GitSessionViewModel?> getViewModel,
        Func<GitBranchInfo?> getSelectedBranch,
        Func<GitSessionViewModel, GitBranchInfo, Task> showBranchLog)
    {
        _owner = owner;
        _logList = logList;
        _branchList = branchList;
        _branchFilterBox = branchFilterBox;
        _logFilterBox = logFilterBox;
        _getViewModel = getViewModel;
        _getSelectedBranch = getSelectedBranch;
        _showBranchLog = showBranchLog;
    }

    internal void OnBranchFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (GitSessionKeyboardMapper.ResolveBranchFilterKey(e.Key, box.Text, hasRows: false)
                    is BranchFilterKeyCommand.Clear)
                {
                    box.Clear();
                    PushFilterTermNow(box);
                }
                else
                {
                    MoveFocusToBranchList();
                }
                break;

            case Key.Down:
                PushFilterTermNow(box);
                if (GitSessionKeyboardMapper.ResolveBranchFilterKey(
                        e.Key, box.Text, _branchList.Items.Count > 0)
                    is not BranchFilterKeyCommand.MoveToList) return;
                MoveFocusToBranchList();
                e.Handled = true;
                break;
        }
    }

    internal async Task OnBranchListKeyDownAsync(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not ModifierKeys.None) return;
        if (e.Key is Key.OemQuestion or Key.Divide)
        {
            e.Handled = true;
            _branchFilterBox.Focus();
            _branchFilterBox.SelectAll();
            return;
        }

        if (e.Key is not Key.Enter || _getViewModel() is not { } vm || _getSelectedBranch() is not { } branch)
            return;
        e.Handled = true;
        await _showBranchLog(vm, branch);
    }

    internal void FocusCommitList()
    {
        _pendingLogG = false;
        if (_logList.Items.Count == 0) { _owner.Focus(); return; }
        _logList.Focus();
        FocusSelectedLogRow();
    }

    /// <summary>ログ絞り込み欄のEsc/Enter/Down操作。Escを消費し、TextBox既定のUndoへ伝播させない。</summary>
    internal void OnLogFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        var command = GitSessionKeyboardMapper.ResolveLogFilterKey(e.Key, box.Text);
        if (command is GitLogFilterKeyCommand.None)
            return;

        e.Handled = true;
        if (command is GitLogFilterKeyCommand.Clear)
        {
            box.Clear();
            // Delay=200 を待たずに反映して、Esc の入力をすぐ一覧へ反映する。
            PushFilterTermNow(box);
        }
        else
        {
            FocusCommitList();
        }
    }

    internal void OnLogListLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _pendingLogG = false;

    internal void OnLogListKeyDown(object sender, KeyEventArgs e)
    {
        var vm = _getViewModel();
        var result = GitSessionKeyboardMapper.ResolveLogKey(
            e.Key, Keyboard.Modifiers, _pendingLogG, vm?.History.HasActiveFilters ?? false);
        _pendingLogG = result.PendingG;

        if (result.Action is GitLogKeyCommand.None)
        {
            if (result.PendingG) e.Handled = true;
            return;
        }

        e.Handled = true;
        switch (result.Action)
        {
            case GitLogKeyCommand.MoveDown:
                SelectLogRowAt(FindCommitIndex(SelectedLogIndex + 1, forward: true));
                break;
            case GitLogKeyCommand.MoveUp:
                SelectLogRowAt(FindCommitIndex(SelectedLogIndex - 1, forward: false));
                break;
            case GitLogKeyCommand.MoveTop:
                SelectLogRowAt(FindCommitIndex(0, forward: true));
                break;
            case GitLogKeyCommand.MoveBottom:
                SelectLogRowAt(FindCommitIndex(_logList.Items.Count - 1, forward: false));
                break;
            case GitLogKeyCommand.OpenDiff:
                if (vm is not null)
                    vm.OpenDiffForCommits(GitCommitSelectionMapper.Commits(_logList.SelectedItems));
                break;
            case GitLogKeyCommand.FocusFilter:
                FocusLogFilter();
                break;
            case GitLogKeyCommand.ClearFilter:
                vm?.History.ClearLogFiltersCommand.Execute(null);
                break;
        }
    }

    internal void FocusLogFilter()
    {
        _logFilterBox.Focus();
        _logFilterBox.SelectAll();
    }

    private void PushFilterTermNow(TextBox box)
        => box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    private void MoveFocusToBranchList()
    {
        _branchList.UpdateLayout();
        if (_branchList.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            first.Focus();
        else
            _branchList.Focus();
    }

    private int SelectedLogIndex
    {
        get
        {
            if (Keyboard.FocusedElement is ListViewItem focused
                && ItemsControl.ItemsControlFromItemContainer(focused) == _logList)
                return _logList.ItemContainerGenerator.IndexFromContainer(focused);
            return _logList.SelectedItem is { } item ? _logList.Items.IndexOf(item) : -1;
        }
    }

    private int FindCommitIndex(int from, bool forward)
    {
        var step = forward ? 1 : -1;
        for (var index = from; index >= 0 && index < _logList.Items.Count; index += step)
            if (_logList.Items[index] is GitLogRow { IsCommit: true })
                return index;
        return -1;
    }

    private void SelectLogRowAt(int index)
    {
        if (index < 0 || index >= _logList.Items.Count) return;
        _logList.SelectedIndex = index;
        _logList.ScrollIntoView(_logList.Items[index]);
        FocusSelectedLogRow();
    }

    private void FocusSelectedLogRow()
    {
        if (_logList.SelectedItem is not { } item) return;
        _logList.UpdateLayout();
        if (_logList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            container.Focus();
    }
}
