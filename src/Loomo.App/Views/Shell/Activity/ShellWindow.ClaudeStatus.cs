using System.IO;
using System.Text.Json.Nodes;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Claude Code の接続の状態（§45.3 P0d）と、待っている知らせ・指摘（§45.4 P1）。新しい画面は作らず、パレットの一覧で見せる。
/// 各行の実行は既存の操作（フックの設定・窓口の登録のコピー・知らせの取り消し）に繋ぐ。
/// </summary>
public partial class ShellWindow {
    private const string ClaudeStatusCategory = "Claude 接続";

    private static PaletteCommand StatusItem(string category, string title, Action run, string detail) =>
        new(category, title, run) { Detail = detail };

    /// <summary>接続の状態を一覧にする。行を選ぶとその場の操作を行う。</summary>
    private void OpenClaudeStatus() {
        var hooks = ClaudeHookInstaller.IsLegacyInstalled() ? "古い版（入れ直すと新しくなります）"
            : ClaudeHookInstaller.IsInstalled() ? "入っている" : "入っていない";
        var window = _loomoMcp is null ? "止まっています" : "動いています";
        var registered = ClaudeCodeRegistered();
        var agents = _paneActivity.AgentTabs().Count;
        var pending = PendingReviewCount();

        var items = new List<PaletteCommand> {
            StatusItem(ClaudeStatusCategory, $"フック：{hooks}", InstallClaudeHooks,
                "承認待ち・セッション・次の頼みごとへの知らせに使います。選ぶと設定し直します。"),
            StatusItem(ClaudeStatusCategory, $"Loomo の窓口（道具）：{window}", CopyLoomoMcpRegistrationCommand,
                _loomoMcp is null
                    ? "Claude Code 連携が切れています。設定の Claude Code 連携を確かめてください。"
                    : "選ぶと、Claude Code に登録するコマンドをコピーします（PowerShell に貼って 1 回実行）。"),
            StatusItem(ClaudeStatusCategory, $"Claude Code への登録：{(registered ? "済み" : "まだ")}", CopyLoomoMcpRegistrationCommand,
                registered ? "~/.claude.json に loomo があります（ユーザー全体）。"
                           : "まだ登録されていません。選ぶとコマンドをコピーします。"),
            StatusItem(ClaudeStatusCategory, $"動いている Claude：{agents} 件", OpenClaudeAgents,
                "このワークスペースのターミナルで動いている Claude Code の一覧を開きます。"),
            StatusItem(ClaudeStatusCategory, $"次の頼みごとに添える知らせ：{pending} 件", OpenReviewNotes,
                "指摘と動きの知らせを一覧にします。選ぶと取り消せます。"),
        };
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>~/.claude.json に loomo の登録があるか（ユーザー全体の登録）。読めなければ「まだ」。</summary>
    private static bool ClaudeCodeRegistered() {
        try {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path))
                return false;
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                && root["mcpServers"] is JsonObject servers && servers.ContainsKey(LoomoMcpServer.ServerName);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return false;
        }
    }

    private int PendingReviewCount()
        => _claudeNotes.Sessions().Sum(session => _claudeNotes.Count(session));

    /// <summary>次の頼みごとに添える知らせを一覧にする。選ぶと、その知らせを取り消す。</summary>
    private void OpenReviewNotes() {
        var items = new List<PaletteCommand>();
        foreach (var session in _claudeNotes.Sessions()) {
            var shortId = session.Length > 8 ? session[..8] : session;
            foreach (var note in _claudeNotes.Peek(session)) {
                var captured = (Session: session, Note: note);
                var firstLine = note.Text.Split('\n')[0];
                items.Add(StatusItem($"知らせ（{note.Kind}・{shortId}）",
                    firstLine.Length > 70 ? firstLine[..70] + "…" : firstLine,
                    () => {
                        if (_claudeNotes.Discard(captured.Session, captured.Note)) {
                            ToastService.Info("知らせを取り消しました");
                            if (_activeEditorTab is { IsRealized: true } active)
                                _pullRequestComments.RefreshExtraMarkers(active.Control);
                        }
                    },
                    note.Text));
            }
        }
        if (items.Count == 0)
            items.Add(new PaletteCommand("知らせ", "次の頼みごとに添える知らせはありません", () => { }));
        OpenCommandPalette(PaletteMode.Command, items);
    }
}
