using System.Windows.Threading;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ターミナルサポートペイン（§39）。前に出ているターミナルのタブを追従元にして、その様子
/// （前面のコマンド・Claude の状態・実行し終えたコマンド）を <see cref="TerminalSupportViewModel"/> へ流す。
/// <para><b>新しく見張るものは無い</b>——材料はタブの様子の表（§24.53 <see cref="TerminalStatusOf"/>）・袖の活動
/// （§24.1.1 <see cref="PaneActivityTracker.AgentTabs"/>）・コマンドの出力の置き場（§24.21）と同じ。
/// 引き直しは合図をまとめて 1 回にし、<b>ペインが見えていないときは何もしない</b>（見えた瞬間に追いつく）。</para>
/// <para>Claude の記録は作業中だけ数秒おきに読み直す。長さと更新時刻が変わっていなければ開きもしない。</para>
/// </summary>
public partial class ShellWindow {
    private readonly TerminalSupportViewModel _terminalSupport = new();
    private bool _terminalSupportRefreshQueued;
    private DispatcherTimer? _terminalSupportClaudeTimer;

    /// <summary>作業中の Claude の記録を読み直す間隔。ターンの終わり・承認待ちは合図で即座に読むので、
    /// これは「作業の途中経過（いま何を書き換えているか）」のための間隔。</summary>
    private static readonly TimeSpan TerminalSupportClaudeInterval = TimeSpan.FromSeconds(4);

    private void InitializeTerminalSupport() {
        TerminalSupportPane.DataContext = _terminalSupport;
        TerminalSupportPaneHost.DataContext = _terminalSupport;
        _terminalSupport.SetDisplayPath(path => _workspace.ToDisplayPath(path));
        _terminalSupport.SetWorkspaceFilter(path => _workspace.Contains(path));

        _terminalSupport.FileOpenRequested += (_, path) => _ = OpenPathInEditorAsync(path, 0, 0);
        _terminalSupport.OutputOpenRequested += (_, output) => _ = OpenTerminalOutputInEditorAsync(output);
        _terminalSupport.OutputCompareRequested += (_, output) => {
            var pair = ActiveTerminalOutputs?.Pairs.FirstOrDefault(p => ReferenceEquals(p.Latest, output));
            if (pair is { } found)
                ShowDiff(new DiffOpenTarget.Comparison(TerminalCommandOutputs.Compare(found.Previous, found.Latest)));
        };
        _terminalSupport.CommandInsertRequested += (_, command) => SendRecentCommandToTerminal(command);
        _terminalSupport.GoToTerminalRequested += (_, _) => {
            if (_activeTerminalTab is { } tab)
                GoToTerminalTab(tab.Id);
        };

        TerminalStatusChanged += RequestTerminalSupportRefresh;
        TerminalStatusTicked += now => {
            if (TerminalSupportPane.IsVisible)
                _terminalSupport.Tick(now);
        };
        // 隠れている間は引き直さないので、見えた瞬間に追いつく。
        TerminalSupportPane.IsVisibleChanged += (_, e) => {
            if (e.NewValue is true)
                RequestTerminalSupportRefresh();
            UpdateTerminalSupportClaudeTimer();
        };
    }

    /// <summary>引き直しを予約する（すでに予約があれば何もしない）。タブの切り替え・Claude の状態・
    /// コマンドの始まりと終わり・出力の切り出しから呼ばれる。</summary>
    private void RequestTerminalSupportRefresh() {
        if (_terminalSupportRefreshQueued || !TerminalSupportPane.IsVisible)
            return;
        _terminalSupportRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshTerminalSupport);
    }

    private void RefreshTerminalSupport() {
        _terminalSupportRefreshQueued = false;
        var subject = BuildTerminalSupportSubject();
        var outputs = ActiveTerminalOutputs;
        var latestWithPrevious = outputs?.Pairs.Select(p => p.Latest).ToHashSet() ?? [];
        _terminalSupport.Update(subject, outputs?.Latest ?? [], latestWithPrevious.Contains, DateTime.UtcNow);
        if (subject?.Mode == TerminalSupportMode.ClaudeCode)
            _ = RefreshTerminalSupportClaudeAsync(subject);
        UpdateTerminalSupportClaudeTimer();
    }

    private TerminalSupportSubject? BuildTerminalSupportSubject() {
        if (_activeTerminalTab is not { } tab)
            return null;
        var status = TerminalStatusOf(tab.Id);
        var agent = _paneActivity.AgentTabs().FirstOrDefault(a => a.TabId == tab.Id);
        var cwd = status.WorkingDirectory is { Length: > 0 } known ? known : tab.View.WorkingDirectory;
        return new TerminalSupportSubject(tab.Id, TerminalTabTitle(tab.Id) ?? "Terminal", cwd,
            status.RunningCommand, status.RunningSinceUtc, agent);
    }

    /// <summary>
    /// このタブの Claude の記録を決めて読み直す（§39.4.2）。SessionStart のフックで結び付いていればそれ（タブ ID 付きで
    /// 届くので確定）。無ければ<b>動いている claude のプロセスから確かめる</b>（<see cref="ClaudeLiveSessions.Match"/>：
    /// この Loomo の端末の中・同じ場所・ほかのタブのものでない）。確かめられなければ何も出さない——
    /// 場所の記録フォルダーの最新を当て推量すると、Loomo の外で動いている別の claude の会話を出してしまう。
    /// </summary>
    private async Task RefreshTerminalSupportClaudeAsync(TerminalSupportSubject subject) {
        if (_paneActivity.AgentSessionOf(subject.TabId) is { TranscriptPath: { Length: > 0 } hooked }) {
            await _terminalSupport.RefreshClaudeAsync(hooked, unresolvedReason: null);
            return;
        }
        var claimed = _terminalTabs
            .Where(t => t.Id != subject.TabId)
            .Select(t => _paneActivity.AgentSessionOf(t.Id)?.SessionId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var typedAt = TerminalProgramCatalog.Match(subject.RunningCommand)?.Id == TerminalProgramCatalog.ClaudeId
            ? subject.RunningSinceUtc : null;
        var clue = new ClaudeTabClue(subject.WorkingDirectory, typedAt);
        var loomoPid = Environment.ProcessId;
        ClaudeSessionMatch match;
        try {
            match = await Task.Run(() => {
                var processes = ClaudeLiveSessions.SnapshotParents();
                return ClaudeLiveSessions.Match(ClaudeLiveSessions.Read(),
                    pid => ClaudeLiveSessions.IsInsideLoomoTerminal(pid, processes, loomoPid), clue, claimed);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) {
            match = new ClaudeSessionMatch(null, $"Claude Code のセッションを確かめられませんでした: {ex.Message}");
        }
        if (_activeTerminalTab?.Id != subject.TabId)
            return;
        await _terminalSupport.RefreshClaudeAsync(
            match.Session is { } session ? ClaudeLiveSessions.TranscriptPath(session) : null, match.Reason);
    }

    /// <summary>Claude が作業中で、ペインが見えている間だけ回す。</summary>
    private void UpdateTerminalSupportClaudeTimer() {
        var working = TerminalSupportPane.IsVisible
            && _activeTerminalTab is { } tab
            && _paneActivity.AgentTabs().FirstOrDefault(a => a.TabId == tab.Id) is { State: ClaudeAgentState.Working };
        if (!working) {
            _terminalSupportClaudeTimer?.Stop();
            return;
        }
        if (_terminalSupportClaudeTimer is null) {
            _terminalSupportClaudeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TerminalSupportClaudeInterval };
            _terminalSupportClaudeTimer.Tick += (_, _) => {
                if (BuildTerminalSupportSubject() is { Mode: TerminalSupportMode.ClaudeCode } subject)
                    _ = RefreshTerminalSupportClaudeAsync(subject);
                UpdateTerminalSupportClaudeTimer();
            };
        }
        if (!_terminalSupportClaudeTimer.IsEnabled)
            _terminalSupportClaudeTimer.Start();
    }

    /// <summary>会話の本文のリンク（ファイル・URL）。振り分けは EditorSupport のプレビューと同じ口。相対パスは
    /// Claude が動いている場所（タブの cwd）から解く——Claude は自分の作業フォルダーからの相対で書く。</summary>
    private void OnTerminalSupportConversationLink(string href) {
        var cwd = BuildTerminalSupportSubject()?.WorkingDirectory;
        var source = cwd is { Length: > 0 } ? Path.Combine(cwd, "claude-conversation.md") : null;
        _ = HandleEditorSupportLinkClickedAsync(href, source);
    }
}
