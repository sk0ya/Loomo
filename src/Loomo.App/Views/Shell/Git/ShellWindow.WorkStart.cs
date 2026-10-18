using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 課題から作業を始める（§24.56）。Work Items 面の行・GitHub の Issue から、課題の番号入りのブランチと
/// ワークツリーを作り（§24.17 の作成ダイアログに初期値を入れて開く）、部屋として開き、望めばその部屋の新しいターミナルで
/// Claude Code を課題の文面つきで起動する。GitKraken Desktop 12.4 の「Issue からエージェントのセッションを始める」の流れ。
/// </summary>
public partial class ShellWindow {
    /// <summary>部屋の切替を待つ上限（切り替わらなければ Claude は今の部屋で起こす）。</summary>
    private static readonly TimeSpan WorkStartRoomTimeout = TimeSpan.FromSeconds(30);
    /// <summary>新しいタブのシェルがプロンプトを出すまで待つ上限。</summary>
    private static readonly TimeSpan WorkStartPromptTimeout = TimeSpan.FromSeconds(20);

    private async Task StartWorkOnTicketAsync(WorkTicket ticket) {
        var git = _vm.GitSession;
        if (!git.IsRepository) {
            ToastService.Error("Git のリポジトリを開いているときだけ、課題から作業を始められます。");
            return;
        }
        try {
            await git.RefreshWorktreesAsync();
            var carry = await git.LoadWorktreeCarryEntriesAsync();
            var seed = new WorktreeCreateSeed(
                WorkTicketStart.BranchName(ticket), WorkTicketStart.Mention(ticket), WorkTicketStart.ClaudePrompt(ticket));
            if (WorktreeCreateDialog.Prompt(this, git, WorkStartBaseBranch(git), null, carry, seed) is not { } result)
                return;
            if (!await git.CreateWorktreeAsync(result.Request, result.OpenAfter, result.CarryOver))
                return;   // 失敗の理由は Git ペインの状態欄に出ている
            git.ReferenceTab = GitReferenceTab.Worktrees;
            if (result.StartClaudePrompt is { } prompt)
                await StartClaudeInWorktreeAsync(result.Request.Path, result.OpenAfter, prompt);
        } catch (Exception ex) when (ex is InvalidOperationException or IOException) {
            ToastService.Error($"課題から作業を始められませんでした: {ex.Message}");
        }
    }

    /// <summary>起点の初期値：既定ブランチ（main / master …）がローカルにあればそれ、無ければいまのブランチ。</summary>
    private static string? WorkStartBaseBranch(GitSessionViewModel git) {
        var locals = git.BranchInfos.Where(b => !b.IsRemote).Select(b => b.Name).ToList();
        return GitCompareArgs.DefaultBranchCandidates.FirstOrDefault(locals.Contains)
            ?? git.BranchInfos.FirstOrDefault(b => b.IsCurrent)?.Name;
    }

    /// <summary>
    /// 作ったワークツリーで Claude Code を起こす。部屋として開いたなら、その部屋に切り替わり終わるのを待ってから
    /// （切替の途中で開いたタブは前の部屋へ紛れ込む・§24.29 の WorkspaceTransitionGate）。新しいタブを作り、
    /// シェルが最初のプロンプトを出してから 1 行を送る——既存のタブへは打たない（§24.3.1：前面が埋まっているかもしれない）。
    /// </summary>
    private async Task StartClaudeInWorktreeAsync(string worktreePath, GitWorktreeOpenMode? openAfter, string prompt) {
        if (openAfter == GitWorktreeOpenMode.Workspace) {
            var deadline = DateTime.UtcNow + WorkStartRoomTimeout;
            while (DateTime.UtcNow < deadline
                   && !(_activeWorkspace?.RootPath is { } root && WorkspacePaths.IsWithin(root, worktreePath)
                        && WorkspacePaths.IsWithin(worktreePath, root)))
                await Task.Delay(150);
            await _workspaceTransition.WhenSettledAsync();
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }

        SetPaneVisible(PaneKind.Terminal, true);
        var tab = CreateTerminalTab(worktreePath, focusOnStart: true);
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, "Claude Code", false);
        ActivateTerminalTab(tab.Id);
        SaveActiveWorkspaceSnapshot();

        if (!await WaitForFirstPromptAsync(tab, WorkStartPromptTimeout)) {
            ToastService.Error("ターミナルの準備が間に合わなかったので、Claude Code は起動しませんでした。");
            return;
        }
        tab.View.SendTerminalInput(WorkTicketStart.PowerShellClaudeCommand(prompt) + "\r");
        FocusPane(PaneKind.Terminal);
    }

    /// <summary>シェル統合の最初のプロンプト（OSC 133;A）を待つ。シェル統合が無いシェルでは来ないので時間で諦める。</summary>
    private static async Task<bool> WaitForFirstPromptAsync(TerminalTab tab, TimeSpan timeout) {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnActivity(object? sender, ShellCommandActivityEventArgs e) {
            if (e.Phase == ShellCommandPhase.PromptStart)
                ready.TrySetResult(true);
        }
        tab.View.ShellCommandActivity += OnActivity;
        try {
            var finished = await Task.WhenAny(ready.Task, Task.Delay(timeout));
            return finished == ready.Task;
        } finally {
            tab.View.ShellCommandActivity -= OnActivity;
        }
    }
}
