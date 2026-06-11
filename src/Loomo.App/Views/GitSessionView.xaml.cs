using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Git セッションペイン。コミットグラフ・ブランチ一覧の表示と、コンテキストメニューからの
/// 複雑な git 操作（rebase / merge / cherry-pick / reset 等）を受け付ける。
/// 名前入力・破壊的操作の確認ダイアログはここ（ビュー）が担い、git 実行は ViewModel に委ねる。
/// </summary>
public partial class GitSessionView : UserControl
{
    public GitSessionView()
    {
        InitializeComponent();
    }

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;

    /// <summary>右クリックでも対象行を選択状態にする（コンテキストメニューの対象を確定させる）。</summary>
    private void OnListRightClickSelect(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
            return;
        var element = e.OriginalSource as DependencyObject;
        // OriginalSource が Run 等の FrameworkContentElement のことがある（VisualTreeHelper だと例外）
        while (element is not null && element is not ListBoxItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        if (element is ListBoxItem item)
            item.IsSelected = true;
    }

    // ===== ブランチ操作 =====

    private GitBranchInfo? SelectedBranch => BranchList.SelectedItem as GitBranchInfo;

    private async void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { IsCurrent: false } branch)
            await vm.CheckoutBranchAsync(branch);
    }

    private async void OnBranchCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.CheckoutBranchAsync(branch);
    }

    private async void OnBranchMerge(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.MergeAsync(branch);
    }

    private async void OnBranchRebase(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"現在のブランチを {branch.Name} の上へリベースします。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "リベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.RebaseAsync(branch);
    }

    private async void OnBranchDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"ブランチ {branch.Name} を削除しますか？",
            "ブランチ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var result = await vm.DeleteBranchAsync(branch, force: false);
        if (result is { Success: false } &&
            result.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            var forceAnswer = MessageBox.Show(Window.GetWindow(this)!,
                $"{branch.Name} はマージされていないコミットを含みます。強制削除（-D）しますか？\nコミットが失われる可能性があります。",
                "ブランチの強制削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (forceAnswer == MessageBoxResult.Yes)
                await vm.DeleteBranchAsync(branch, force: true);
        }
    }

    private async void OnNewBranch(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ", "ブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateBranchAsync(name);
    }

    // ===== コミット操作 =====

    private GitLogRow? SelectedCommit =>
        LogList.SelectedItem is GitLogRow { IsCommit: true } row ? row : null;

    private async void OnCommitCreateBranch(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            $"コミット {row.ShortHash} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateBranchAsync(name, row.Hash);
    }

    private async void OnCommitCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.CheckoutCommitAsync(row);
    }

    private async void OnCommitCherryPick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.CherryPickAsync(row);
    }

    private async void OnCommitRevert(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.RevertAsync(row);
    }

    private async void OnCommitResetSoft(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.ResetAsync(row, GitResetMode.Soft);
    }

    private async void OnCommitResetMixed(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.ResetAsync(row, GitResetMode.Mixed);
    }

    private async void OnCommitResetHard(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} まで hard リセットします。作業ツリー・インデックスの変更はすべて失われます。\n実行しますか？",
            "リセット (hard)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.ResetAsync(row, GitResetMode.Hard);
    }

    private async void OnCommitOpenPatch(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.OpenPatchAsync(row);
    }

    private void OnCommitCopyHash(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { Hash: { } hash })
        {
            try { Clipboard.SetText(hash); } catch { /* クリップボード占有中は無視 */ }
        }
    }
}
