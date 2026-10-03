using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Git ペインの「操作ログ」面。取り戻す・戻す操作はコミット一覧と同じ
/// <see cref="GitSessionOperationController"/> へ、記録を <see cref="GitReflogRow.ToLogRow"/> で渡して通す
/// （確認ダイアログの文面や実行中表示まで、履歴の面と同じにするため）。
/// </summary>
public partial class GitReflogView : UserControl
{
    public GitReflogView() => InitializeComponent();

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;
    private GitReflogRow? Selected => Vm?.Reflog.SelectedRow;

    private string? Prompt(GitOperationPrompt request) => InputDialog.Prompt(
        Window.GetWindow(this), request.Title, request.Message, request.InitialValue,
        allowEmpty: request.AllowEmpty, multiline: request.Multiline);

    private bool Confirm(GitOperationConfirmation request) =>
        MessageBox.Show(Window.GetWindow(this), request.Message, request.Title,
            MessageBoxButton.YesNo,
            request.Severity == GitConfirmationSeverity.Warning
                ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ===== 一覧 =====

    private void OnListRightClickSelect(object sender, MouseButtonEventArgs e)
        => GitSessionSelectionPresenter.SelectRowFromContextMenu(e.OriginalSource);

    /// <summary>ダブルクリック＝そのコミットの差分（履歴の面と同じ）。見出し・余白では何もしない。</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        ShowCommitDiff();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected is not null)
        {
            ShowCommitDiff();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Selected is { } row)
        {
            ClipboardText.Set(row.Entry.Hash);
            e.Handled = true;
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { } vm && vm.Reflog.Filter.Length > 0)
        {
            vm.Reflog.Filter = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ReflogList.Items.Count > 0)
        {
            // 絞り込んだあと手をキーボードから離さずに一覧へ降りる（ブランチの絞り込み欄と同じ）。
            if (ReflogList.SelectedIndex < 0) ReflogList.SelectedIndex = 0;
            (ReflogList.ItemContainerGenerator.ContainerFromIndex(ReflogList.SelectedIndex) as UIElement)?.Focus();
            e.Handled = true;
        }
    }

    /// <summary>行が無い所（見出し・余白）ではメニューを出さず、意味を成さない項目は無効にする。</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Selected is not { } row)
        {
            e.Handled = true;
            return;
        }
        MenuOperationDiff.IsEnabled = row.Entry.MovedCommit;
        // どの ref からも辿れないコミットは履歴の一覧に出てこない＝押しても見つからない。
        MenuShowInHistory.IsEnabled = !row.IsLost;
    }

    // ===== 見る =====

    private void OnShowCommitDiff(object sender, RoutedEventArgs e) => ShowCommitDiff();

    private void ShowCommitDiff()
    {
        if (Vm is { } vm && Selected is { } row)
            vm.OpenDiffForCommits(new[] { row.ToLogRow() });
    }

    private void OnShowOperationDiff(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && Selected is { } row)
            vm.OpenReflogOperationDiff(row);
    }

    private void OnCompareWorkingTree(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && Selected is { } row)
            vm.CompareCommitWithWorkingTree(row.ToLogRow());
    }

    private async void OnShowInHistory(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && Selected is { } row)
            await vm.ShowReflogCommitInHistoryAsync(row);
    }

    // ===== 取り戻す・戻す =====

    private async void OnCreateBranch(object sender, RoutedEventArgs e) =>
        await Execute(GitCommitOperation.CreateBranch);

    private async void OnCheckout(object sender, RoutedEventArgs e) =>
        await Execute(GitCommitOperation.Checkout);

    private async void OnResetSoft(object sender, RoutedEventArgs e) => await Execute(GitCommitOperation.ResetSoft);
    private async void OnResetMixed(object sender, RoutedEventArgs e) => await Execute(GitCommitOperation.ResetMixed);
    private async void OnResetHard(object sender, RoutedEventArgs e) => await Execute(GitCommitOperation.ResetHard);

    /// <summary>「この状態へ戻す ▾」は左クリックで方式のメニューを開く（1クリックでリセットを走らせない）。</summary>
    private void OnResetButtonClick(object sender, RoutedEventArgs e)
    {
        if (ResetButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ResetButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private Task Execute(GitCommitOperation operation) => Selected is { } row
        ? GitSessionOperationController.ExecuteCommitAsync(
            Vm, row.ToLogRow(), operation, prompt: Prompt, confirm: Confirm)
        : Task.CompletedTask;

    // ===== 写す =====

    private void OnCopyHash(object sender, RoutedEventArgs e) => ClipboardText.Set(Selected?.Entry.Hash);
    private void OnCopySelector(object sender, RoutedEventArgs e) => ClipboardText.Set(Selected?.Selector);
    private void OnCopyDescription(object sender, RoutedEventArgs e) => ClipboardText.Set(Selected?.Description);

    // ===== 詳細の変更ファイル =====

    private CommitFileStat? SelectedFile => DetailFileList.SelectedItem as CommitFileStat?;

    private void OnDetailFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        OpenFileDiffWindow();
    }

    private void OnDetailFileDiffWindow(object sender, RoutedEventArgs e) => OpenFileDiffWindow();

    private void OpenFileDiffWindow()
    {
        if (Vm is { } vm && Selected is { } row && SelectedFile is { } file)
            vm.RequestReflogFileDiffWindow(row, file.Path);
    }

    private void OnDetailFileCopyPath(object sender, RoutedEventArgs e) => ClipboardText.Set(SelectedFile?.Path);
}
