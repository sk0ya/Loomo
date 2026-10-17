using System.Text.Json.Nodes;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: Claude Code のフックを聞く（§24.1.1・§38.4）。
/// <list type="bullet">
/// <item>Notification（承認を求める）→ 袖のバッジを「● Claude 承認待ち」にし、Loomo が裏ならタスクバーで呼ぶ。
///   タイトルの印だけでは「ターンの終わり」と「承認待ち」がどちらも ✳ で区別できない。</item>
/// <item>SessionStart → どのタブがどのセッションかを覚える。ターミナル見出しのコンテキスト量と、セッションの再開に使う。</item>
/// </list>
/// フックはユーザー全体の設定（<c>~/.claude/settings.json</c>）に入るので、入れる・外すはパレットから明示的に行う。
/// スクリプトは Loomo のターミナルの外では何もしない。
/// </summary>
public partial class ShellWindow {
    private ClaudeHookBridge? _claudeHooks;
    private string? _claudeHookPipeName;

    private void InitializeClaudeHooks() {
        try {
            _claudeHooks = new ClaudeHookBridge();
            _claudeHooks.HookReceived += (tab, hookEvent) =>
                Dispatcher.BeginInvoke(() => OnClaudeHook(tab, hookEvent));
            _claudeHooks.Start();
            _claudeHookPipeName = _claudeHooks.PipeName;
        } catch (Exception ex) {
            System.Diagnostics.Trace.WriteLine($"Claude Code のフックを聞けませんでした: {ex.Message}");
        }
    }
    private void DisposeClaudeHooks() => _claudeHooks?.Dispose();

    private void OnClaudeHook(Guid tabId, JsonObject hookEvent) {
        if (_terminalTabs.All(tab => tab.Id != tabId))
            return;   // 別の Loomo のタブ（パイプ名はユーザーで共有）か、閉じたタブ
        switch ((string?)hookEvent["hook_event_name"]) {
            case "SessionStart" when (string?)hookEvent["session_id"] is { Length: > 0 } sessionId:
                _paneActivity.TerminalAgentSessionStarted(tabId, sessionId, (string?)hookEvent["transcript_path"]);
                RefreshClaudeContext();
                break;
            case "Notification" when ClaudeHookBridge.IsPermissionRequest(hookEvent):
                if (_paneActivity.TerminalAgentNeedsApproval(tabId, IsTerminalPaneWatched()))
                    UpdatePaneActivityBadge(PaneKind.Terminal);
                if (!IsActive)
                    FlashTaskbarUntilForeground();
                break;
        }
    }

    // ── ターミナル見出しのコンテキスト量（statusline 相当） ───────────────
    /// <summary>前に出ているタブが Claude のセッションなら、その記録から「いまのコンテキストの大きさ」を読んで
    /// 見出しに出す。ターンの終わりとタブの切り替えで読み直す（記録は数 MB になり得るので裏で読む）。</summary>
    private void RefreshClaudeContext() {
        if (_activeTerminalTab is not { } tab || _paneActivity.AgentSessionOf(tab.Id) is not { TranscriptPath: { Length: > 0 } path }) {
            _vm.ClaudeContext = "";
            return;
        }
        var tabId = tab.Id;
        _ = Task.Run(() => ClaudeSessionCatalog.Read(path)).ContinueWith(task => {
            if (_activeTerminalTab?.Id != tabId)
                return;
            var summary = task.IsCompletedSuccessfully ? task.Result : null;
            _vm.ClaudeContext = summary?.ContextTokens is { } tokens ? $"⧉ {ClaudeSessionCatalog.FormatTokens(tokens)}" : "";
            _vm.ClaudeContextToolTip = summary is null ? "" : string.Join(Environment.NewLine, new[] {
                summary.Title is { Length: > 0 } title ? $"セッション: {title}" : null,
                summary.ContextTokens is { } t ? $"コンテキスト: {t:N0} トークン（最後の応答の時点）" : null,
                summary.Model is { Length: > 0 } model ? $"モデル: {model}" : null,
            }.Where(line => line is not null));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── パレット ─────────────────────────────────────────────────
    private void InstallClaudeHooks() {
        try {
            var added = ClaudeHookInstaller.Install();
            MessageBox.Show(this, added
                    ? $"Claude Code のフックを設定しました。\n{ClaudeHookInstaller.SettingsPath()}\n\n次に起動する claude から、承認待ちが袖のバッジに出ます。元の設定は settings.json.loomo-backup に残してあります。"
                    : "Claude Code のフックは設定済みです。",
                "Claude Code", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            MessageBox.Show(this, $"フックを設定できませんでした: {ex.Message}", "Claude Code", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void UninstallClaudeHooks() {
        try {
            var removed = ClaudeHookInstaller.Uninstall();
            MessageBox.Show(this, removed ? "Claude Code のフックを外しました。" : "Loomo のフックは設定されていません。",
                "Claude Code", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            MessageBox.Show(this, $"フックを外せませんでした: {ex.Message}", "Claude Code", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>「Claude Code のセッションを再開…」：このワークスペースのセッションを新しい順に並べ、選んだものを
    /// 新しいターミナルタブで <c>claude --resume</c> する。常駐セッション（§34）が切れたあとでも会話へ戻れる。</summary>
    private async void OpenClaudeSessions() {
        var sessions = await Task.Run(() => ClaudeSessionCatalog.List(_workspace));
        var now = DateTime.UtcNow;
        var items = sessions.Select(session => new PaletteCommand("Claude セッション",
            session.Title is { Length: > 0 } title ? title : session.LastPrompt ?? session.SessionId,
            () => ResumeClaudeSession(session.SessionId)) {
            Badge = session.ContextTokens is { } tokens ? ClaudeSessionCatalog.FormatTokens(tokens) : null,
            Detail = string.Join(Environment.NewLine, new[] {
                $"最終更新: {RelativeAge(now - session.LastWriteUtc)}",
                session.LastPrompt is { Length: > 0 } prompt ? $"最後の頼みごと: {prompt}" : null,
                $"ID: {session.SessionId}",
            }.Where(line => line is not null)),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand("Claude セッション", "このワークスペースの Claude Code セッションは見つかりませんでした", () => { }));
        OpenCommandPalette(PaletteMode.Command, items);
    }
    private static string RelativeAge(TimeSpan age)
        => age.TotalMinutes < 1 ? "たった今"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分前"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} 時間前"
            : $"{(int)age.TotalDays} 日前";

    private void ResumeClaudeSession(string sessionId) {
        OnTerminalNewTab(this, new RoutedEventArgs());
        if (_activeTerminalTab is not { } tab)
            return;
        RunWhenShellReady(tab, $"claude --resume {sessionId}");
        FocusPane(PaneKind.Terminal);
    }

    /// <summary>新しいタブのシェルがプロンプトを出したら（OSC133 の A）コマンドを打つ。シェル統合が来ないシェル
    /// のために 5 秒で諦めて打つ。</summary>
    private void RunWhenShellReady(TerminalTab tab, string command) {
        var sent = false;
        void Send() {
            if (sent)
                return;
            sent = true;
            tab.View.ShellCommandActivity -= OnActivity;
            tab.View.SendTerminalInput(command + "\r");
        }
        void OnActivity(object? sender, ShellCommandActivityEventArgs e) {
            if (e.Phase == ShellCommandPhase.PromptStart)
                Dispatcher.BeginInvoke(Send);
        }
        tab.View.ShellCommandActivity += OnActivity;
        var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        fallback.Tick += (_, _) => {
            fallback.Stop();
            Send();
        };
        fallback.Start();
    }
}
