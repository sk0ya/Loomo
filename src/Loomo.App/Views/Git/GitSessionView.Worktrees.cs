using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// GitSessionView: ワークツリー（左列下段の「WT」タブ）と、比較の入口（2点比較・ブランチ／コミットの
/// 「作業ツリーと比較」）。ダイアログと確認はここ、git と比較の組み立ては VM
/// （<see cref="GitSessionViewModel"/> のワークツリー部分）。
/// </summary>
public partial class GitSessionView
{
    private GitWorktreeInfo? SelectedWorktree => WorktreeList.SelectedItem as GitWorktreeInfo;

    // ===== 作成 =====

    private async void OnWorktreeCreate(object sender, RoutedEventArgs e) => await CreateWorktreeAsync();

    private async void OnBranchCreateWorktree(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is { } branch)
            await CreateWorktreeAsync(existingBranch: branch.Name);
    }

    private async void OnCommitCreateWorktree(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { Hash: { } hash })
            await CreateWorktreeAsync(startPoint: hash);
    }

    private async Task CreateWorktreeAsync(string? startPoint = null, string? existingBranch = null)
    {
        if (Vm is not { } vm) return;
        if (WorktreeCreateDialog.Prompt(Window.GetWindow(this), vm, startPoint, existingBranch) is not { } result)
            return;
        if (await vm.CreateWorktreeAsync(result.Request, result.OpenAfter))
            vm.ReferenceTab = GitReferenceTab.Worktrees;   // 作ったものが見える場所へ
    }

    // ===== 比較 =====

    private void OnWorktreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedWorktree is { IsCurrent: false, Exists: true } worktree)
            Vm?.CompareWithWorktree(worktree);
    }

    private void OnWorktreeCompare(object sender, RoutedEventArgs e)
    {
        if (SelectedWorktree is { } worktree)
            Vm?.CompareWithWorktree(worktree);
    }

    /// <summary>見出しの「⇄ 比較」。左は既定ブランチ、右はいまのワークツリー（＝右側をその場で直せる形）を初期値にする。</summary>
    private async void OnComparePoints(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        await ComparePointsAsync(vm, DefaultBranchEndpoint(vm), CurrentWorktreeEndpoint(vm));
    }

    /// <summary>行の「2点比較…」。左にその行、右にいまのワークツリー（その行自身なら右は空）。</summary>
    private async void OnWorktreeComparePoints(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedWorktree is not { } worktree) return;
        var from = GitCompareEndpoint.Worktree(worktree);
        await ComparePointsAsync(vm, from, worktree.IsCurrent ? null : CurrentWorktreeEndpoint(vm));
    }

    private async Task ComparePointsAsync(GitSessionViewModel vm, GitCompareEndpoint? from, GitCompareEndpoint? to)
    {
        if (ComparePointsDialog.Prompt(Window.GetWindow(this), vm.CompareEndpoints(), from, to) is not { } result)
            return;
        await vm.ComparePointsAsync(result.From, result.To, result.FromMergeBase);
    }

    private static GitCompareEndpoint? CurrentWorktreeEndpoint(GitSessionViewModel vm)
        => vm.CurrentWorktree is { } current ? GitCompareEndpoint.Worktree(current) : null;

    /// <summary>左の初期値。いまのブランチ以外で main / master があればそれ（「main からどれだけ進んだか」が
    /// いちばん多い問い）。</summary>
    private static GitCompareEndpoint? DefaultBranchEndpoint(GitSessionViewModel vm)
    {
        var locals = vm.BranchInfos.Where(b => !b.IsRemote && !b.IsCurrent).Select(b => b.Name).ToList();
        var name = GitCompareArgs.DefaultBranchCandidates.FirstOrDefault(locals.Contains);
        return name is null ? null : GitCompareEndpoint.Ref(name);
    }

    private void OnBranchCompareWorkingTree(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is { } branch)
            Vm?.CompareBranchWithWorkingTree(branch, fromMergeBase: false);
    }

    private void OnBranchCompareMergeBase(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is { } branch)
            Vm?.CompareBranchWithWorkingTree(branch, fromMergeBase: true);
    }

    private void OnCommitCompareWorkingTree(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { } row)
            Vm?.CompareCommitWithWorkingTree(row);
    }

    // ===== 開く =====

    private void OnWorktreeOpenWorkspace(object sender, RoutedEventArgs e) => Open(GitWorktreeOpenMode.Workspace);
    private void OnWorktreeAddFolder(object sender, RoutedEventArgs e) => Open(GitWorktreeOpenMode.AddFolder);
    private void OnWorktreeTerminal(object sender, RoutedEventArgs e) => Open(GitWorktreeOpenMode.Terminal);
    private void OnWorktreeExplorer(object sender, RoutedEventArgs e) => Open(GitWorktreeOpenMode.Explorer);

    private void Open(GitWorktreeOpenMode mode)
    {
        if (SelectedWorktree is { } worktree)
            Vm?.OpenWorktree(worktree, mode);
    }

    private void OnWorktreeCopyPath(object sender, RoutedEventArgs e) => ClipboardText.Set(SelectedWorktree?.Path);

    // ===== 管理 =====

    private async void OnWorktreesRefresh(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RefreshWorktreesAsync();
    }

    private async void OnWorktreesPrune(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.PruneWorktreesAsync();
    }

    private async void OnWorktreeLock(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedWorktree is not { } worktree) return;
        var reason = PromptGitOperation(new GitOperationPrompt(
            "ワークツリーのロック",
            $"{worktree.DisplayName} をロックします。理由（任意）：\n" +
            "ロック中は「掃除」で消されず、うっかり削除もできなくなります（外付けドライブ等に置いたとき向け）。",
            AllowEmpty: true));
        if (reason is null) return;
        await vm.LockWorktreeAsync(worktree, reason);
    }

    private async void OnWorktreeUnlock(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedWorktree is { } worktree)
            await vm.UnlockWorktreeAsync(worktree);
    }

    /// <summary>
    /// 削除。未コミットの変更があるときは、それが消えることを警告してから強制で消す
    /// （git は変更のあるワークツリーを --force なしでは消さない）。ブランチは残す——ブランチの削除は
    /// ブランチ一覧の仕事で、ここで一緒に消すと「ワークツリーを片付けたら作業も消えた」になる。
    /// </summary>
    private async void OnWorktreeRemove(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedWorktree is not { } worktree) return;
        if (!worktree.CanRemove || worktree.IsCurrent)
            return;
        if (worktree.IsLocked)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"{worktree.DisplayName} はロックされています。先に「ロック解除」してください。",
                "ワークツリーの削除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var branchNote = worktree.Branch is { } branch ? $"\nブランチ「{branch}」は残ります。" : "";
        var dirty = worktree.IsDirty;
        var message = dirty
            ? $"{worktree.DisplayName} には未コミットの変更が {worktree.ChangeCount} 件あります。\n" +
              $"削除するとそれらは失われます（復元できません）。\n\n{worktree.Path}{branchNote}\n\n削除しますか？"
            : $"{worktree.DisplayName} を削除しますか？\nフォルダーごと消えます。\n\n{worktree.Path}{branchNote}";
        if (!ConfirmGitOperation(new GitOperationConfirmation("ワークツリーの削除", message,
                dirty ? GitConfirmationSeverity.Warning : GitConfirmationSeverity.Question)))
            return;

        var result = await vm.RemoveWorktreeAsync(worktree, force: dirty);
        // 一覧の件数が古かった（数えた後に向こうで編集された）ときは git が拒むので、改めて聞く。
        if (result is { Success: false } && !dirty && GitWorktreeArgs.RemoveNeedsForce(result.Message)
            && ConfirmGitOperation(new GitOperationConfirmation("ワークツリーの削除",
                $"{worktree.DisplayName} に未コミットの変更か未追跡のファイルがあります。\n" +
                "それらごと削除しますか？（復元できません）", GitConfirmationSeverity.Warning)))
            await vm.RemoveWorktreeAsync(worktree, force: true);
    }

    /// <summary>行の状態に合わせてメニューを出し分ける（押せるのに何も起きない項目を作らない）。
    /// 行の外（空白）での右クリックはメニューごと出さない。</summary>
    private void OnWorktreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedWorktree is not { } worktree)
        {
            e.Handled = true;
            return;
        }
        var other = !worktree.IsCurrent && worktree.Exists;
        WorktreeMenuCompare.IsEnabled = other;
        WorktreeMenuOpenWorkspace.IsEnabled = other;
        WorktreeMenuAddFolder.IsEnabled = other;
        WorktreeMenuTerminal.IsEnabled = worktree.Exists;
        WorktreeMenuExplorer.IsEnabled = worktree.Exists;
        WorktreeMenuLock.Visibility = !worktree.IsLocked && !worktree.IsMain ? Visibility.Visible : Visibility.Collapsed;
        WorktreeMenuUnlock.Visibility = worktree.IsLocked ? Visibility.Visible : Visibility.Collapsed;
        WorktreeMenuRemove.IsEnabled = worktree.CanRemove && !worktree.IsCurrent;
    }
}
