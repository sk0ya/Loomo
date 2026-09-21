
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Services.Infrastructure;
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
    private GitBranchTreeMenuController _branchMenuController = null!;
    private GitSelectedLogRowPresenter _selectedLogRowPresenter = null!;
    private GitSessionColumnVisibilityController _columnVisibilityController = null!;
    private GitCommitFileInteractionPresenter _commitFilePresenter = null!;
    private readonly GitBranchLogRequestHandler _branchLogRequests = new();

    public GitSessionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        _selectedLogRowPresenter = new GitSelectedLogRowPresenter(LogList);
        _commitFilePresenter = new GitCommitFileInteractionPresenter(CommitFileList, () => Vm);
        _columnVisibilityController = new GitSessionColumnVisibilityController(
            BranchSplitterColumn, BranchColumn, BranchSplitter, BranchListArea, BranchOpListGroup, BranchOpBar,
            CommitDetailSplitterColumn, CommitDetailColumn, CommitDetailSplitter, CommitDetailPanel);
        _branchMenuController = new GitBranchTreeMenuController(
            BranchList, () => Vm?.HasRemote == true,
            BranchMenuCheckout, BranchMenuMerge, BranchMenuRebase, BranchMenuDelete,
            BranchMenuDeleteRemote, BranchMenuSetUpstream, BranchMenuUnsetUpstream,
            BranchMenuPull, BranchMenuPush, BranchMenuPushForce,
            deferSingleClick: true, mergeStrategy: BranchMenuMergeStrategy,
            operationCheckout: BranchOpCheckout,
            operationMerge: BranchOpMerge,
            operationDelete: BranchOpDelete);
        _keyboardController = new GitSessionKeyboardController(
            this, LogList, BranchList, BranchFilterBox, LogFilterBox,
            () => Vm, () => SelectedTreeBranch, ShowBranchLogGuardedAsync);
        SetupLogColumnResize();
        SetupLogFilter();
        // 無選択で立ち上がるので、選んだ1本に効く操作は最初から押せない状態にしておく
        _branchMenuController.UpdateSelectionActions(SelectedTreeBranch);
    }

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;

    private string? PromptGitOperation(GitOperationPrompt request) => InputDialog.Prompt(
        Window.GetWindow(this), request.Title, request.Message, request.InitialValue,
        allowEmpty: request.AllowEmpty, multiline: request.Multiline);

    private bool ConfirmGitOperation(GitOperationConfirmation request) =>
        MessageBox.Show(Window.GetWindow(this), request.Message, request.Title,
            MessageBoxButton.YesNo,
            request.Severity == GitConfirmationSeverity.Warning
                ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ===== コミット詳細（変更ファイル一覧）のリンク描画 =====

    /// <summary>DataContext（VM）の差し替えに追従し、CommitDetail の変化を購読し直す。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _selectedLogRowPresenter.Attach(Vm?.History);
        _columnVisibilityController.Attach(Vm);
    }

    /// <summary>
    /// 左列下段の種類切替（タグ／リモート／サブモジュール）。
    ///
    /// <para><b>選択中のボタンを押しても外れないようにする</b>のがここの主な仕事。ToggleButton は
    /// Click が届く前に自分で IsChecked を反転させてしまうので、同じ種類を押し直すと
    /// 「ボタンは OFF なのに一覧はその種類のまま」になる——VM の値は変わらず、通知も飛ばないので
    /// 片方向バインディングは二度と true を押し戻さない。押されたボタンだけ結び直して実体へ揃える
    /// （種類が実際に変わったときは通知で3つとも揃うので、これで足りる）。</para>
    /// </summary>
    private void OnReferenceTabClick(object sender, RoutedEventArgs e)
        => GitSessionSelectionPresenter.SelectReferenceTab(Vm, sender);

    /// <summary>操作バーのいちばん上の開閉ボタン。VM 側を反転させ、
    /// 表示の反映（列を畳む／戻す）と永続化はそちらの変更通知経由で行う。</summary>
    private void OnBranchColumnToggleClick(object sender, RoutedEventArgs e)
        => _columnVisibilityController.ToggleBranchColumn();

    private void OnCommitFileDoubleClick(object sender, MouseButtonEventArgs e)
        => _commitFilePresenter.OnDoubleClick(e);

    private void OnCommitFileDiffWindow(object sender, RoutedEventArgs e)
        => _commitFilePresenter.OpenDiffWindow();

    private async void OnCommitFileOpen(object sender, RoutedEventArgs e)
        => await _commitFilePresenter.OpenFileAsync();

    private void OnCommitFileContextMenuOpening(object sender, ContextMenuEventArgs e)
        => _commitFilePresenter.OnContextMenuOpening(e);

    private void OnCommitFileCopyPath(object sender, RoutedEventArgs e) =>
        _commitFilePresenter.CopyPath();

    /// <summary>右クリックでも対象行を選択状態にする（コンテキストメニューの対象を確定させる）。</summary>
    private void OnListRightClickSelect(object sender, MouseButtonEventArgs e)
        => GitSessionSelectionPresenter.SelectRowFromContextMenu(e.OriginalSource);

    // ===== ブランチ操作 =====

    /// <summary>ツリーで選択中のブランチ。フォルダノード選択中は null（各操作は何もしない）。</summary>
    private GitBranchInfo? SelectedTreeBranch => (BranchList.SelectedItem as BranchTreeNode)?.Branch;
    private GitBranchInfo? SelectedBranch => _branchMenuController.Target ?? SelectedTreeBranch;

    /// <summary>
    /// ブランチのダブルクリックはチェックアウトではなく、右側のコミットグラフをそのブランチに切り替える
    /// （ブランチの切り替え自体はヘッダーのブランチ切替コントロールから行う）。
    /// </summary>
    private async void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        // 連打中は SelectedItem が次のクリックで変わっている可能性があるため、
        // TreeView の現在選択ではなく、ダブルクリックされた行を対象にする。
        var branch = GitSessionSelectionPresenter.ResolveDoubleClickedBranch(
            e.OriginalSource, SelectedTreeBranch);
        if (branch is null)
            return;

        await ShowBranchLogGuardedAsync(vm, branch);
    }

    /// <summary>
    /// そのブランチのコミットを一覧に出す（ダブルクリックと Enter の共通経路）。
    /// async void のイベントハンドラーから例外を出すとアプリ全体が終了するので必ず受け、
    /// 連打で古い読み込みが失敗したときは<b>最新の操作のぶんだけ</b>を画面へ知らせる
    /// （さもないと、もう表示していないブランチのエラーが後から居座る）。
    /// </summary>
    private Task ShowBranchLogGuardedAsync(GitSessionViewModel vm, GitBranchInfo branch)
        => _branchLogRequests.ShowAsync(vm, branch, _branchMenuController.CancelPendingMenu);

    private async void OnShowAllBranchesLog(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.ShowAllBranchesLogAsync();
    }

    /// <summary>
    /// 対象が無い（フォルダ・見出しを右クリックした）ならメニューごと出さない。ブランチ行なら、
    /// そのブランチに意味を成さない項目を無効化する（自分自身へのチェックアウト／マージ／リベース、
    /// 現在ブランチの削除、リモートブランチの削除＝git branch -d では消せない）。
    /// タイトルバーのブランチ切替と同じコントローラーで可否を決める。
    /// </summary>
    private async void OnBranchPushForce(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch,
            GitBranchOperation.ForcePush,
            confirmForcePush: target => GitBranchDialogs.ConfirmForcePush(Window.GetWindow(this), target));

    private async void OnBranchDeleteRemote(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch,
            GitBranchOperation.DeleteRemote,
            confirmRemoteDelete: target => GitBranchDialogs.ConfirmDeleteRemoteBranch(Window.GetWindow(this), target));

    private async void OnBranchSetUpstream(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch,
            GitBranchOperation.SetUpstream,
            promptUpstream: (vm, branch) => GitBranchDialogs.PromptUpstream(Window.GetWindow(this), vm, branch));

    private async void OnBranchUnsetUpstream(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.UnsetUpstream);

    /// <summary>ダブルクリックと同じ「右のコミットグラフをこのブランチに切り替える」を右クリックからも。</summary>
    private async void OnBranchShowLog(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.ShowLog);

    private async void OnBranchCheckout(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.Checkout);

    private async void OnBranchMerge(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.Merge);

    private async void OnBranchMergeFastForwardOnly(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.MergeFastForwardOnly);

    private async void OnBranchMergeNoFastForward(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.MergeNoFastForward);

    private async void OnBranchMergeSquash(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.MergeSquash);

    private async void OnBranchRebase(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch,
            GitBranchOperation.Rebase, confirm: ConfirmGitOperation);

    private async void OnBranchCreateFrom(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch,
            GitBranchOperation.CreateFrom, prompt: PromptGitOperation);

    private void OnBranchCopyName(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedBranch?.Name);

    private async void OnBranchPull(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.Pull);

    private async void OnBranchPush(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch, GitBranchOperation.Push);

    private async void OnBranchDelete(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteBranchAsync(
            Vm, SelectedBranch,
            GitBranchOperation.Delete, confirm: ConfirmGitOperation);

    // ===== 操作バー（ブランチ一覧の左の縦帯）=====

    /// <summary>
    /// 選んだ1本に効く操作（チェックアウト／マージ／削除）の可否を選択に合わせる。
    /// 判定は行の右クリックメニューと同じ——同じ操作が
    /// 入口ごとに違う可否を見せると、押せないのか効かないのか分からなくなる。
    /// </summary>
    private void OnBranchSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _branchMenuController.UpdateSelectionActions(SelectedTreeBranch);

    /// <summary>
    /// 新しいブランチ。起点は一覧で選んだブランチで、選んでいなければ現在ブランチ（＝素の
    /// <c>git branch</c>）——「押したのに何も起きない」を作らないため、無選択でも必ず作れる。
    /// </summary>
    private async void OnBarBranchCreate(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.CreateBranchAsync(
            Vm, SelectedTreeBranch, PromptGitOperation);

    private async void OnBarPullMerge(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.PullWithModeAsync(Vm, GitPullMode.Merge);
    private async void OnBarPullRebase(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.PullWithModeAsync(Vm, GitPullMode.Rebase);
    private async void OnBarPullFastForward(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.PullWithModeAsync(Vm, GitPullMode.FastForwardOnly);

    private async void OnBarPushNormal(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.Commands.PushAsync();
    }

    /// <summary>強制プッシュ。相手は上流（表示が無ければ現在ブランチ）で、選択行とは関係がない。</summary>
    private async void OnBarPushForce(object sender, RoutedEventArgs e)
        => await GitSessionOperationController.ForcePushCurrentBranchAsync(
            Vm, target => GitBranchDialogs.ConfirmForcePush(Window.GetWindow(this), target));

    /// <summary>
    /// 一覧の開閉をまとめて切り替える。TreeViewItem ではなくモデル（<see cref="BranchTreeNode"/>）を
    /// 書き換えるのは、畳まれた枝のコンテナはまだ存在しないため——見えている行だけ開いても
    /// 「すべて」にならない。
    /// </summary>
    private void OnBarExpandAll(object sender, RoutedEventArgs e) => SetBranchTreeExpanded(true);
    private void OnBarCollapseAll(object sender, RoutedEventArgs e) => SetBranchTreeExpanded(false);

    private void SetBranchTreeExpanded(bool expanded)
    {
        if (Vm?.PaneFilteredBranchTree is { } tree)
            BranchTreeBuilder.SetExpandedAll(tree, expanded);
    }

    // ===== タグ操作 =====

    private GitTagInfo? SelectedTag => TagList.SelectedItem as GitTagInfo;

    private async void OnTagDoubleClick(object sender, MouseButtonEventArgs e) =>
        await GitSessionOperationController.ExecuteTagAsync(
            Vm, SelectedTag, GitTagOperation.Checkout);

    private async void OnTagCheckout(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteTagAsync(
            Vm, SelectedTag, GitTagOperation.Checkout);

    private async void OnTagPush(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteTagAsync(
            Vm, SelectedTag, GitTagOperation.Push);

    private void OnTagCopyName(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedTag?.Name);

    private async void OnTagDelete(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteTagAsync(
            Vm, SelectedTag,
            GitTagOperation.Delete, confirm: ConfirmGitOperation);

    private async void OnTagCreate(object sender, RoutedEventArgs e) => await CreateTagAsync(target: null);

    private async void OnCommitCreateTag(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { } row)
            await CreateTagAsync(row.Hash);
    }

    private async void OnTagsPushAll(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteTagAsync(
            Vm, null, GitTagOperation.PushAll);

    /// <summary>タグ名（必須）→メッセージ（任意）の順に入力を取り、作成する。</summary>
    private async Task CreateTagAsync(string? target)
    {
        await GitSessionOperationController.CreateTagAsync(Vm, target, PromptGitOperation);
    }

    // ===== サブモジュール操作 =====

    private GitSubmoduleInfo? SelectedSubmodule => SubmoduleList.SelectedItem as GitSubmoduleInfo;

    private async void OnSubmoduleInit(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteSubmoduleAsync(

            Vm, SelectedSubmodule, GitSubmoduleOperation.Init);

    private async void OnSubmoduleUpdate(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteSubmoduleAsync(

            Vm, SelectedSubmodule, GitSubmoduleOperation.Update);

    private async void OnSubmodulesSync(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteSubmoduleAsync(
            Vm, null, GitSubmoduleOperation.Sync);

    private void OnSubmoduleCopyPath(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedSubmodule?.Path);

    // ===== コミット操作 =====

    private GitLogRow? SelectedCommit => GitCommitSelectionMapper.Commit(LogList.SelectedItem);

    private IReadOnlyList<GitLogRow> SelectedCommits => GitCommitSelectionMapper.Commits(LogList.SelectedItems);

    /// <summary>
    /// コミット一覧を末尾付近までスクロールしたら次ページを追加読み込みする（無限スクロール）。
    /// 下方向のスクロール（またはビューポート縮小）でのみ判定し、追加読み込み後の伸長で連鎖発火しないよう
    /// 純粋な内容伸長（VerticalChange・ViewportHeightChange が 0）は無視する。仮想化は既定の行単位スクロール
    /// なので Extent/Offset/Viewport は行数単位だが、末尾までの残り行数で判定する式は同じく成立する。
    /// </summary>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm is { } vm && GitLogPagingPolicy.ShouldLoadMore(
                e.VerticalChange, e.ViewportHeightChange, e.ExtentHeight, e.VerticalOffset, e.ViewportHeight))
            _ = vm.History.LoadMoreAsync();
    }

    /// <summary>選択コミットの差分を Diff セッションへ（1件=コミットの変更、複数=端点間の比較）。</summary>
    private void OnCommitShowDiff(object sender, RoutedEventArgs e)
        => GitSessionSelectionPresenter.OpenCommitDiff(Vm, LogList.SelectedItems);

    private async void OnCommitCreateBranch(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.CreateBranch, prompt: PromptGitOperation);

    private async void OnCommitCheckout(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.Checkout);

    private async void OnCommitRewriteMessage(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.RewriteMessage, prompt: PromptGitOperation, confirm: ConfirmGitOperation);

    /// <summary>
    /// コミット一覧のコンテキストメニューを開く直前に、意味を持たない項目を落とす——スカッシュは2件以上、
    /// インタラクティブリベースとファイル履歴の「この版の…」は単一選択時だけ、GitHub の項目は
    /// GitHub リポジトリのときだけ、作者の絞り込みは既にその作者だけを見ていないときだけ。
    /// </summary>
    private void OnCommitContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        GitSessionMenuPolicyMapper.ApplyCommitMenu(
            Vm, SelectedCommit, SelectedCommits.Count,
            SquashMenuItem, InteractiveRebaseMenuItem,
            FileRevisionSeparator, FileRevisionMenuItem,
            OpenOnHostingMenuItem, CopyHostingUrlMenuItem, FilterByAuthorMenuItem);
    }

    // ===== 特定リビジョンのファイル（ファイル履歴中のみ） =====

    private async void OnCommitOpenFileRevision(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.OpenFileRevision);

    private async void OnCommitCompareFileRevision(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.CompareFileRevision);

    private async void OnCommitRestoreFileRevision(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.RestoreFileRevision, confirm: ConfirmGitOperation);

    // ===== リモート =====

    private async void OnRemoteAdd(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteRemoteAsync(
            Vm, null, GitRemoteOperation.Add,
            prompt: PromptGitOperation);

    private async void OnRemoteSetUrl(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteRemoteAsync(
            Vm,
            RemoteList.SelectedItem as GitRemoteInfo, GitRemoteOperation.SetUrl,
            prompt: PromptGitOperation);

    private void OnRemoteCopyUrl(object sender, RoutedEventArgs e) =>
        ClipboardText.Set((RemoteList.SelectedItem as GitRemoteInfo)?.Url);

    private async void OnRemoteRemove(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteRemoteAsync(
            Vm,
            RemoteList.SelectedItem as GitRemoteInfo, GitRemoteOperation.Remove,
            confirm: ConfirmGitOperation);

    /// <summary>
    /// 選択コミットからHEADまでをインタラクティブリベースする。候補取得→確認→ダイアログ→実行の順。
    /// </summary>
    private async void OnCommitInteractiveRebase(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.InteractiveRebase,
            confirm: ConfirmGitOperation,
            showRebasePlan: entries => InteractiveRebaseDialog.Show(Window.GetWindow(this), entries),
            showError: ToastService.Error);

    /// <summary>選択した複数コミットを1つにまとめる（squash）。履歴を書き換えるので確認を取る。</summary>
    private async void OnCommitSquash(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, null, GitCommitOperation.Squash,
            selectedRows: SelectedCommits, prompt: PromptGitOperation, confirm: ConfirmGitOperation);

    private async void OnCommitCherryPick(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.CherryPick);

    private async void OnCommitCherryPickNoCommit(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.CherryPickNoCommit);

    private async void OnCommitRevert(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.Revert);

    private async void OnCommitRevertNoCommit(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.RevertNoCommit);

    private async void OnCommitResetSoft(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.ResetSoft);

    private async void OnCommitResetMixed(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.ResetMixed);

    private async void OnCommitResetHard(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit,
            GitCommitOperation.ResetHard, confirm: ConfirmGitOperation);

    // ===== 調べる・たどる =====

    /// <summary>一覧の絞り込みをこのコミットの作者だけに切り替える（絞り込み帯の作者欄と同じ状態になる）。</summary>
    private void OnCommitFilterByAuthor(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            vm.FilterByAuthor(row);
    }

    private void OnCommitOpenOnHosting(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            vm.OpenCommitOnHosting(row);
    }

    // ===== パッチ・コピー =====

    private async void OnCommitOpenPatch(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.ExecuteCommitAsync(
            Vm, SelectedCommit, GitCommitOperation.OpenPatch);

    private async void OnCommitCopyPatch(object sender, RoutedEventArgs e) =>
        await GitSessionOperationController.CopyCommitPatchAsync(

            Vm, SelectedCommit, ClipboardText.Set);

    private void OnCommitCopyHash(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedCommit?.Hash);

    private void OnCommitCopyShortHash(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedCommit?.ShortHash);

    private void OnCommitCopySubject(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedCommit?.Subject);

    private void OnCommitCopyAuthor(object sender, RoutedEventArgs e) =>
        ClipboardText.Set(SelectedCommit?.Author);

    /// <summary>「0c92f1e 件名」——issue やレビューに貼るときの定型。</summary>
    private void OnCommitCopySummary(object sender, RoutedEventArgs e)
        => ClipboardText.Set(GitCommitSelectionMapper.Summary(SelectedCommit));

    private void OnCommitCopyHostingUrl(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            ClipboardText.Set(vm.CommitHostingUrl(row));
    }

}
