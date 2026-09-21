using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>一覧と絞り込み欄にまたがるファイルカラムのキーボード操作を管理する。</summary>
internal sealed class FilesColumnKeyboardInteractionController
{
    private readonly ListBox _list;
    private readonly TextBox _filterBox;
    private readonly Func<FilesColumnViewModel?> _getViewModel;
    private readonly FilesColumnCommandController _commands;
    private readonly Action _showProperties;
    private readonly Action<FileEntryViewModel?> _renameEntry;
    private readonly Action<IReadOnlyList<FileEntryViewModel>> _deleteEntries;
    private readonly DispatcherTimer _typeAheadResetTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private string _typeAheadText = string.Empty;

    internal FilesColumnKeyboardInteractionController(
        ListBox list,
        TextBox filterBox,
        Func<FilesColumnViewModel?> getViewModel,
        FilesColumnCommandController commands,
        Action showProperties,
        Action<FileEntryViewModel?> renameEntry,
        Action<IReadOnlyList<FileEntryViewModel>> deleteEntries)
    {
        _list = list;
        _filterBox = filterBox;
        _getViewModel = getViewModel;
        _commands = commands;
        _showProperties = showProperties;
        _renameEntry = renameEntry;
        _deleteEntries = deleteEntries;
        _typeAheadResetTimer.Tick += (_, _) => ResetTypeAhead();
    }

    internal void StopTypeAheadTimer() => _typeAheadResetTimer.Stop();

    internal void FocusList()
    {
        if (_list.Items.Count > 0 && _list.SelectedIndex < 0)
            _list.SelectedIndex = 0;
        var container = _list.ItemContainerGenerator.ContainerFromIndex(
            Math.Max(0, _list.SelectedIndex)) as ListBoxItem;
        if (container is not null)
            container.Focus();
        else
            _list.Focus();
    }

    internal void OnListPreviewKeyDown(KeyEventArgs e)
    {
        if (_getViewModel() is not { } vm)
            return;

        if (_commands.HandleKeyDown(
                vm,
                e,
                Selection,
                SingleSelection,
                () => _list.SelectedItem as FileEntryViewModel,
                _showProperties,
                OpenFilter,
                MoveSelection,
                _renameEntry,
                _deleteEntries))
            e.Handled = true;
    }

    internal void OnFilterKeyDown(KeyEventArgs e)
    {
        if (_getViewModel() is not { } vm)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseFilter();
                FocusList();
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Down:
                // 絞り込みは効かせたまま一覧へ戻る。
                FocusList();
                e.Handled = true;
                break;
        }
    }

    /// <summary>表示順（絞り込み・並べ替え・グループ化の後）の隣へ選択を移す。</summary>
    private void MoveSelection(int delta)
    {
        var items = _list.Items.OfType<FileEntryViewModel>().ToList();
        var currentIndex = _list.SelectedItem is FileEntryViewModel current
            ? items.IndexOf(current)
            : -1;
        var index = FolderTreeKeyboardNavigation.FindAdjacentIndex(items.Count, currentIndex, delta);
        if (index < 0)
            return;
        _list.SelectedItems.Clear();
        _list.SelectedItem = items[index];
        _list.ScrollIntoView(items[index]);
    }

    /// <summary>一覧への文字入力を Explorer と同じ type-ahead 選択として扱う。</summary>
    internal void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        var items = _list.Items.OfType<FileEntryViewModel>().ToList();
        if (items.Count == 0)
            return;

        _typeAheadResetTimer.Stop();
        var currentIndex = _list.SelectedItem is FileEntryViewModel current
            ? items.IndexOf(current)
            : -1;
        var search = FolderTreeKeyboardNavigation.ResolveTypeAheadSearch(
            items, entry => entry.Name, _typeAheadText, e.Text, currentIndex);
        _typeAheadText = search.Input;

        if (search.MatchIndex >= 0)
        {
            _list.SelectedItems.Clear();
            _list.SelectedItem = items[search.MatchIndex];
            _list.ScrollIntoView(items[search.MatchIndex]);
        }

        _typeAheadResetTimer.Start();
        e.Handled = true;
    }

    private void OpenFilter()
    {
        if (_getViewModel() is not { } vm)
            return;
        vm.IsFilterBarOpen = true;
        // 出したばかりのバーはまだ配置されていないので、レイアウト後にフォーカスする。
        _filterBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _filterBox.Focus();
            _filterBox.SelectAll();
        }));
    }

    private List<FileEntryViewModel> Selection()
        => _list.SelectedItems.OfType<FileEntryViewModel>().ToList();

    private FileEntryViewModel? SingleSelection()
    {
        var selection = Selection();
        return selection.Count == 1 ? selection[0] : null;
    }

    private void ResetTypeAhead()
    {
        _typeAheadResetTimer.Stop();
        _typeAheadText = string.Empty;
    }
}
