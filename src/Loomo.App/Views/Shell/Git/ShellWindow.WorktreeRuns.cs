using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ワークツリーの行から決まったコマンドを回す（§24.104・GitLens 19.2.0 の Run Default Task）。
/// <para>これまでの「ターミナルでこの場所へ移動」は表示ターミナルへ cd を送るだけで、並べて進めているワークツリーのどれでテストが通っているかは、
/// 部屋を移るかタブを渡り歩いて見るしかなかった。ここでは<b>新しい</b>ターミナルのタブ（既存のタブには打たない・§24.3.1）でそのワークツリーを場所に
/// コマンドを回し、終了コード（OSC 133;D）を Git ペインの行に「✓ dotnet test」「✗ dotnet test」として戻す。回っている間は「▶」。
/// タブはふつうのターミナルなので、袖のバッジ・動いているもの面（§24.54）・見ていない間に終わった通知（§24.97）にもそのまま乗る。</para>
/// <para>コマンドはリポジトリ（メインのワークツリー）ごとに 1 つ覚え、次は入力欄に入れて出す（<see cref="WorktreeRuns.Remember"/>）。勝手には回さない。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>シェルの最初のプロンプトを待つ長さ。シェル統合の無いシェルでは来ないので、そこで諦めて知らせる。</summary>
    private static readonly TimeSpan WorktreeRunPromptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>ワークツリーのパス → そこで最後に回したもの（このセッションの間だけ）。</summary>
    private readonly Dictionary<string, WorktreeRun> _worktreeRuns = new(StringComparer.OrdinalIgnoreCase);

    private async Task RunCommandInWorktreeAsync(string worktreePath) {
        var repository = _vm.GitSession.MainWorktreePath ?? worktreePath;
        var store = WorktreeRuns.DefaultStorePath;
        var remembered = WorktreeRuns.RememberedCommand(store, repository);
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(worktreePath));
        if (InputDialog.Prompt(this, "このワークツリーでコマンドを実行",
                $"「{folder}」を場所にした新しいターミナルのタブで回します（コマンドはこのリポジトリ用に覚えます）",
                remembered ?? "") is not { Length: > 0 } command)
            return;
        try { WorktreeRuns.Remember(store, repository, command); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 覚えられなくても回す */ }

        SetPaneVisible(PaneKind.Terminal, true);
        var tab = CreateTerminalTab(worktreePath, focusOnStart: true);
        tab.CustomName = $"▶ {folder}";
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false, tab.CustomName);
        ActivateTerminalTab(tab.Id);
        SaveActiveWorkspaceSnapshot();

        if (!await WaitForFirstPromptAsync(tab, WorktreeRunPromptTimeout)) {
            ToastService.Error("ターミナルの準備が間に合わなかったので、コマンドは回しませんでした。");
            return;
        }

        var key = Path.TrimEndingDirectorySeparator(worktreePath);
        _worktreeRuns[key] = new WorktreeRun(key, command, DateTime.UtcNow);
        RefreshWorktreeRunMarks();

        // 打った 1 回だけを見る（そのあと人がこのタブで打ったものは、このワークツリーの「決まったコマンド」ではない）。
        var executed = false;
        void OnActivity(object? sender, ShellCommandActivityEventArgs e) {
            if (e.Phase == ShellCommandPhase.CommandExecuted) {
                executed = true;
                return;
            }
            if (e.Phase != ShellCommandPhase.CommandDone || !executed)
                return;
            tab.View.ShellCommandActivity -= OnActivity;
            var exitCode = e.ExitCode;
            Dispatcher.BeginInvoke(() => {
                if (_worktreeRuns.TryGetValue(key, out var run) && run.Command == command && run.IsRunning)
                    _worktreeRuns[key] = run with { ExitCode = exitCode, FinishedUtc = DateTime.UtcNow };
                RefreshWorktreeRunMarks();
            });
        }
        tab.View.ShellCommandActivity += OnActivity;
        tab.View.SendTerminalInput(command + "\r");
        FocusPane(PaneKind.Terminal);
    }

    private void RefreshWorktreeRunMarks()
        => _vm.GitSession.WorktreeRunMarks = WorktreeRuns.Marks(_worktreeRuns.Values);
}
