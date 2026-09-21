
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ブランチ切替ポップアップの中身。タイトルバーと Git ペインヘッダーの両方が同じものを載せる
/// （以前は同じマークアップが2箇所に写経されていて、右クリックメニューだけを共用リソースに逃がすために
/// PlacementTarget から実体を引き当てる回りくどい作りになっていた）。
///
/// 構成は上から「同期帯（フェッチ／プル／プッシュ）」「絞り込み」「ブランチ一覧」「新規作成」。
/// 同期帯を一覧の外に固定しているのは操作の対象が違うから——フェッチはリモート、プル／プッシュは
/// 現在ブランチと上流に効くもので、一覧で選んだブランチには関係がない。一覧の行（と右クリック）は
/// 逆に、選んだ1本にだけ効く操作に限る。
///
/// DataContext は <see cref="GitSessionViewModel"/>（Git ペインと共有）。git 実行は VM に委ね、
/// 名前入力・破壊的操作の確認ダイアログはここが担う。成功時の表示更新は
/// GitService.RepositoryChanged → RefreshAsync の既存経路に乗る。
/// </summary>
public partial class BranchSwitcherView : UserControl
{
    private GitBranchTreeMenuController _branchMenuController = null!;

    public BranchSwitcherView()
    {
        InitializeComponent();
        _branchMenuController = new GitBranchTreeMenuController(
            Tree, () => Vm?.HasRemote == true,
            MenuCheckout, MenuMerge, MenuRebase, MenuDelete, MenuDeleteRemote,
            MenuSetUpstream, MenuUnsetUpstream, MenuPull, MenuPush, MenuPushForce);
    }

    /// <summary>ポップアップを閉じてほしい（チェックアウト成功・ダイアログを出す直前など）。
    /// 実際に閉じるのは Popup を持つ側（ShellWindow）。</summary>
    public event EventHandler? CloseRequested;

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 開く直前の初期化。前回の絞り込みが残っていると「ブランチが消えた」ように見えるので毎回消し、
    /// そのままキーボードで絞り込めるようフォーカスを入れる。
    /// </summary>
    public void PrepareForOpen()
    {
        StatusText.Visibility = Visibility.Collapsed;
        if (Vm is { } vm)
            vm.BranchFilter = "";
        // ポップアップが開いてレイアウトされた後でないとフォーカスが入らない
        Dispatcher.BeginInvoke(new Action(() => FilterBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowError(string message)
    {
        StatusText.Text = message.Trim();
        StatusText.Visibility = Visibility.Visible;
    }

    private string? PromptGitOperation(GitOperationPrompt request) => InputDialog.Prompt(
        Window.GetWindow(this), request.Title, request.Message, request.InitialValue,
        allowEmpty: request.AllowEmpty, multiline: request.Multiline);

    private bool ConfirmGitOperation(GitOperationConfirmation request) =>
        MessageBox.Show(Window.GetWindow(this), request.Message, request.Title,
            MessageBoxButton.YesNo,
            request.Severity == GitConfirmationSeverity.Warning
                ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    private async Task ExecuteSelectedBranchOperationAsync(GitBranchOperation operation)
    {
        if (Vm is not { } vm || Target is not { } branch)
            return;
        var owner = Window.GetWindow(this);
        Close();
        await GitSessionOperationController.ExecuteBranchAsync(vm, branch, operation,
            confirmForcePush: target => GitBranchDialogs.ConfirmForcePush(owner, target),
            confirmRemoteDelete: target => GitBranchDialogs.ConfirmDeleteRemoteBranch(owner, target),
            promptUpstream: (session, target) => GitBranchDialogs.PromptUpstream(owner, session, target),
            prompt: PromptGitOperation,
            confirm: ConfirmGitOperation);
    }

    /// <summary>Esc で絞り込みを消す（空ならポップアップごと閉じる）。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Vm is { BranchFilter.Length: > 0 } vm)
            vm.BranchFilter = "";
        else
            Close();
        e.Handled = true;
    }

    // ===== 一覧 =====

    // ===== 行の右クリックメニュー =====

    private BranchTreeNode? SelectedNode => Tree.SelectedItem as BranchTreeNode;
    private GitBranchInfo? Target => _branchMenuController.Target ?? SelectedNode?.Branch;

    // ===== 同期帯の「▾」（方式を選ぶ） =====

    /// <summary>ボタンに付けたメニューを左クリックで開く（右クリック専用のままだと誰も気づかない）。</summary>
    private static void OpenMenu(object sender)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;
        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnPullOptions(object sender, RoutedEventArgs e) => OpenMenu(sender);
    private void OnPushOptions(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private Task PullWithModeAsync(GitPullMode mode) =>
        Vm is { } vm ? vm.PullWithModeAsync(mode) : Task.CompletedTask;

    private async void OnPullMerge(object sender, RoutedEventArgs e) =>
        await PullWithModeAsync(GitPullMode.Merge);
    private async void OnPullRebase(object sender, RoutedEventArgs e) =>
        await PullWithModeAsync(GitPullMode.Rebase);
    private async void OnPullFastForward(object sender, RoutedEventArgs e) =>
        await PullWithModeAsync(GitPullMode.FastForwardOnly);

    private async void OnPushNormal(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        await vm.Commands.PushAsync();
    }

    private async void OnPushForce(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var owner = Window.GetWindow(this);
        var target = GitBranchActionPolicy.ForcePushTarget(vm.UpstreamLabel);
        Close();
        if (GitBranchDialogs.ConfirmForcePush(owner, target))
            await vm.PushForceAsync();
    }

    private async void OnMenuCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Target is not { } branch) return;
        var result = await vm.Commands.CheckoutBranchAsync(branch);
        if (result is { Success: true })
            Close();
        else if (result is not null)
            ShowError(result.Message);
    }

    private async void OnMenuMerge(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.Merge);

    private async void OnMenuRebase(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.Rebase);

    private async void OnMenuCreateFrom(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.CreateFrom);

    private void OnMenuCopyName(object sender, RoutedEventArgs e)
    {
        if (Target is { } branch)
            ClipboardText.Set(branch.Name);
        Close();
    }

    private async void OnMenuPull(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.Pull);

    private async void OnMenuPush(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.Push);

    private async void OnMenuPushForce(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.ForcePush);

    private async void OnMenuDeleteRemote(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.DeleteRemote);

    private async void OnMenuSetUpstream(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.SetUpstream);

    private async void OnMenuUnsetUpstream(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.UnsetUpstream);

    private async void OnMenuDelete(object sender, RoutedEventArgs e) =>
        await ExecuteSelectedBranchOperationAsync(GitBranchOperation.Delete);

    // ===== 新規作成 =====

    private async void OnNewBranchClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        Close();
        await GitSessionOperationController.CreateBranchAsync(vm, start: null, prompt: PromptGitOperation);
    }
}
