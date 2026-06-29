using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
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
        var element = e.OriginalSource as DependencyObject;
        // OriginalSource が Run 等の FrameworkContentElement のことがある（VisualTreeHelper だと例外）
        while (element is not null and not ListBoxItem and not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        if (element is ListBoxItem listItem)
            listItem.IsSelected = true;
        else if (element is TreeViewItem treeItem)
            treeItem.IsSelected = true;
    }

    // ===== ブランチ操作 =====

    /// <summary>ツリーで選択中のブランチ。フォルダノード選択中は null（各操作は何もしない）。</summary>
    private GitBranchInfo? SelectedBranch => (BranchList.SelectedItem as BranchTreeNode)?.Branch;

    /// <summary>
    /// ブランチのダブルクリックはチェックアウトではなく、右側のコミットグラフをそのブランチに切り替える
    /// （ブランチの切り替え自体はヘッダーのブランチ切替コントロールから行う）。
    /// </summary>
    private async void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.ShowBranchLogAsync(branch);
    }

    /// <summary>
    /// フォルダ行はクリック一回で開閉する（リーフ＝ブランチ行は選択のまま：ダブルクリックでログ表示）。
    /// 展開矢印（ToggleButton, ClickMode=Press）上のクリックは既に開閉済みなので二重に反応しない。
    /// </summary>
    private void OnBranchTreeClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton)
                return;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (element is TreeViewItem { DataContext: BranchTreeNode { IsFolder: true } } item)
            item.IsExpanded = !item.IsExpanded;
    }

    private async void OnShowAllBranchesLog(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.ShowAllBranchesLogAsync();
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

    // ===== コミット操作 =====

    private GitLogRow? SelectedCommit =>
        LogList.SelectedItem is GitLogRow { IsCommit: true } row ? row : null;

    /// <summary>選択コミットの差分を Diff セッションへ（1件=コミットの変更、複数=端点間の比較）。</summary>
    private void OnCommitShowDiff(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var rows = LogList.SelectedItems.OfType<GitLogRow>().Where(r => r.IsCommit).ToList();
        vm.OpenDiffForCommits(rows);
    }

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

    private async void OnCommitRewriteMessage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var current = await vm.GetCommitMessageAsync(row);
        var message = InputDialog.Prompt(Window.GetWindow(this), "コミットメッセージを修正",
            $"{row.ShortHash} のコミットメッセージを入力してください。\nこのコミット以降の履歴が書き換わります。",
            current, multiline: true);
        if (message is null || string.Equals(message, current, StringComparison.Ordinal))
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} 以降のコミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "コミットメッセージを修正", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.RewriteCommitMessageAsync(row, message);
    }

    /// <summary>選択中のコミット件数（グラフ継続行は除く）。</summary>
    private int SelectedCommitCount =>
        LogList.SelectedItems.OfType<GitLogRow>().Count(r => r.IsCommit);

    /// <summary>コミット一覧のコンテキストメニューを開く直前：スカッシュは2件以上の選択時だけ見せる。</summary>
    private void OnCommitContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var visible = SelectedCommitCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SquashMenuItem.Visibility = visible;
        SquashSeparator.Visibility = visible;
    }

    /// <summary>選択した複数コミットを1つにまとめる（squash）。履歴を書き換えるので確認を取る。</summary>
    private async void OnCommitSquash(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var rows = LogList.SelectedItems.OfType<GitLogRow>().Where(r => r.IsCommit).ToList();
        if (rows.Count < 2)
            return;  // メニューは2件以上のときだけ出るが念のため
        var combinedMessage = await vm.GetCombinedCommitMessageAsync(rows);
        var message = InputDialog.Prompt(Window.GetWindow(this), "スカッシュ後のコミットメッセージ",
            "スカッシュ後に使用するコミットメッセージを編集してください。",
            combinedMessage, multiline: true);
        if (message is null)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"選択した {rows.Count} 件のコミットを1つにまとめます。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "スカッシュ", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.SquashAsync(rows, message);
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
