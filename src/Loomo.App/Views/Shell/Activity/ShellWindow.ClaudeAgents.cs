using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: いま動いている Claude Code の一覧（§38.8）。VS Code の Agents ウィンドウのセッション一覧に
/// あたるもの。袖のバッジ（§24.1.1）はペイン単位の集約なので、Claude を複数のタブ（ワークツリーごと等）で
/// 回していると「どれが自分を待っているか」が分からない——それをタブ単位に開いて並べ、選べばそのタブへ行く。
/// </summary>
public partial class ShellWindow
{
    private async void OpenClaudeAgents()
    {
        var agents = _paneActivity.AgentTabs()
            .Select(agent => (Agent: agent, Tab: _terminalTabs.FirstOrDefault(t => t.Id == agent.TabId)))
            .Where(pair => pair.Tab is not null)
            .ToList();
        if (agents.Count == 0)
        {
            OpenCommandPalette(PaletteMode.Command, [
                new PaletteCommand("Claude Code", "このワークスペースのターミナルで動いている Claude Code はありません", () => { }),
                new PaletteCommand("Claude Code", "Claude Code のセッションを再開…", OpenClaudeSessions,
                    _keybindings.For("claude.resumeSession")?.Format(), "claude.resumeSession"),
            ]);
            return;
        }

        // コンテキスト量は記録（数 MB になり得る）から読むので裏で。読めなくても一覧は出す。
        var summaries = await Task.Run(() => agents.ToDictionary(
            pair => pair.Agent.TabId,
            pair => pair.Agent.TranscriptPath is { Length: > 0 } path ? TryReadSession(path) : null));

        var items = agents.Select(pair =>
        {
            var (agent, tab) = (pair.Agent, pair.Tab!);
            var summary = summaries.GetValueOrDefault(agent.TabId);
            var tabName = tab.CustomName ?? tab.View.HeaderTitle;
            var title = agent.Title ?? summary?.Title ?? tabName;
            var tokens = summary?.ContextTokens is { } t ? $" ⧉ {ClaudeSessionCatalog.FormatTokens(t)}" : "";
            return new PaletteCommand("Claude Code", title, () => GoToClaudeAgent(agent.TabId))
            {
                Badge = ClaudeAgentTab.Label(agent.State) + tokens,
                Detail = string.Join(Environment.NewLine, new[]
                {
                    $"状態: {ClaudeAgentTab.Label(agent.State)}",
                    $"タブ: {tabName}",
                    tab.View.WorkingDirectory is { Length: > 0 } cwd ? $"場所: {cwd}" : null,
                    summary?.LastPrompt is { Length: > 0 } prompt ? $"最後の頼みごと: {prompt}" : null,
                    summary?.ContextTokens is { } context ? $"コンテキスト: {context:N0} トークン（最後の応答の時点）" : null,
                    agent.SessionId is { } id ? $"ID: {id}" : null,
                }.Where(line => line is not null)),
            };
        }).ToList();
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private static ClaudeSessionSummary? TryReadSession(string transcriptPath)
    {
        try { return ClaudeSessionCatalog.Read(transcriptPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void GoToClaudeAgent(Guid tabId)
    {
        if (_terminalTabs.All(tab => tab.Id != tabId))
            return;
        FocusPane(PaneKind.Terminal);
        ActivateTerminalTab(tabId);
    }
}
