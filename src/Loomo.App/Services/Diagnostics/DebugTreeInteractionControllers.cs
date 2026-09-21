using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>問題ツリーの行選択を、グループ開閉または診断ナビゲーションへ振り分ける。</summary>
internal sealed class DebugProblemsInteractionController(
    TreeView tree,
    Func<ProblemsViewModel?> resolveViewModel)
{
    public void HandleMouseUp(MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source)
            return;
        if (WpfTreeTraversal.FindAncestor<ToggleButton>(source) is not null
            || WpfTreeTraversal.FindAncestor<ButtonBase>(source) is not null
            || WpfTreeTraversal.FindAncestor<TreeViewItem>(source) is not { } treeItem)
            return;

        switch (treeItem.DataContext)
        {
            case ProblemFileGroup group:
                group.IsExpanded = !group.IsExpanded;
                break;
            case ProblemItemViewModel item:
                resolveViewModel()?.OpenCommand.Execute(item);
                break;
        }
    }

    public void HandleKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        switch (tree.SelectedItem)
        {
            case ProblemItemViewModel item:
                resolveViewModel()?.OpenCommand.Execute(item);
                e.Handled = true;
                break;
            case ProblemFileGroup group:
                group.IsExpanded = !group.IsExpanded;
                e.Handled = true;
                break;
        }
    }
}

/// <summary>変数の編集操作とウォッチ式追加を、デバッグ検査VMへ反映する。</summary>
internal sealed class DebugVariablesInteractionController(Func<DebugInspectionViewModel?> resolveViewModel)
{
    public void BeginEdit(object? sender)
    {
        if ((sender as FrameworkElement)?.DataContext is DebugVariableViewModel variable)
            variable.BeginEdit();
    }

    public void BeginLeafEdit(MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            is DebugVariableViewModel { CanEdit: true, HasChildren: false } variable)
        {
            variable.BeginEdit();
            e.Handled = true;
        }
    }

    public static void FocusEditor(object? sender)
    {
        if (sender is TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    public static void HandleEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DebugVariableViewModel variable })
            return;
        if (e.Key == Key.Enter)
        {
            _ = variable.CommitEditAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            variable.CancelEdit();
            e.Handled = true;
        }
    }

    public static void CommitOnLostFocus(object? sender)
    {
        if (sender is TextBox { DataContext: DebugVariableViewModel variable })
            _ = variable.CommitEditAsync();
    }

    public void HandleWatchKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && resolveViewModel() is { } viewModel
            && viewModel.AddWatchCommand.CanExecute(null))
        {
            viewModel.AddWatchCommand.Execute(null);
            e.Handled = true;
        }
    }
}

/// <summary>失敗テストのソース移動とテストグループの開閉を処理する。</summary>
internal sealed class DebugTestsInteractionController(Func<ITestExplorer?> resolveViewModel)
{
    public void NavigateToTestSource(MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            is TestItemViewModel item)
            resolveViewModel()?.NavigateToTestSource(item);
    }

    public static void ToggleGroup(object? sender)
    {
        if (sender is FrameworkElement { DataContext: TestGroupViewModel group })
            group.IsExpanded = !group.IsExpanded;
    }
}

/// <summary>デバッグ実行対象一覧の行ダブルクリックとEnterを、該当VMのコマンドへ接続する。</summary>
internal sealed class DebugLaunchListController<TViewModel, TEntry>(
    Func<TViewModel?> resolveViewModel,
    Func<TViewModel, ICommand> resolveCommand)
    where TViewModel : class
    where TEntry : class
{
    public void RunDoubleClickedEntry(MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is TEntry entry)
            Execute(entry);
    }

    public void RunSelectedEntryOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: TEntry entry })
        {
            if (Execute(entry))
                e.Handled = true;
        }
    }

    private bool Execute(TEntry entry)
    {
        if (resolveViewModel() is not { } viewModel)
            return false;
        var command = resolveCommand(viewModel);
        if (!command.CanExecute(entry))
            return false;
        command.Execute(entry);
        return true;
    }
}

/// <summary>構成ビューの対象一覧からのアタッチ実行と再読み込みをまとめる。</summary>
internal sealed class DebugAttachListController<TViewModel>(
    Func<TViewModel?> resolveViewModel,
    Action<TViewModel> refresh,
    Func<TViewModel, ICommand> resolveAttachCommand,
    Func<TViewModel, bool>? canAttach = null)
    where TViewModel : class
{
    public void Refresh()
    {
        if (resolveViewModel() is { } viewModel)
            refresh(viewModel);
    }

    public void AttachFromDoubleClick(MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null
            || resolveViewModel() is not { } viewModel
            || canAttach?.Invoke(viewModel) == false)
            return;

        var command = resolveAttachCommand(viewModel);
        if (command.CanExecute(null))
            command.Execute(null);
    }
}
