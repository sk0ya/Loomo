using System.Text;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Claude への指摘（§45.4 P1）。エディタのカーソルの行に宛てて指摘を書く。指摘は、このワークスペースで動いている
/// Claude のセッションの<b>次の頼みごと</b>に添えられる（UserPromptSubmit の文脈）。Claude の画面には何も打たない（主役は人間、§38.1）。
/// <para>線引き：見えるターミナルに打たない・書き換えない・フォーカスを動かさない（入力は既存の複数行の入力ダイアログ）。
/// 送るのは次の頼みごとまで待つ——作業中の Claude へ割り込ませない。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>人間がいま見ているもの（§45.5 P2 `what_human_sees`）。UI スレッドで読む。中身は選択の範囲だけ。</summary>
    internal LoomoRoomResult DescribeHumanView() {
        const int MaxSelection = 4000;
        var sb = new StringBuilder();
        sb.AppendLine($"Front pane: {_focusedRegion?.Pane.ToString() ?? "unknown"}");
        if (_activeEditorTab is { IsRealized: true } tab && tab.Control.FilePath is { Length: > 0 } path) {
            sb.AppendLine($"Editor file: {_workspace.ToDisplayPath(path)}{(tab.Control.IsModified ? "（未保存の変更あり）" : "")}");
            sb.AppendLine($"Caret line: {tab.Control.Caret.Line + 1}");
            if (tab.Control.HasSelection && tab.Control.SelectedText is { Length: > 0 } selected) {
                var shown = selected.Length > MaxSelection ? selected[..MaxSelection] + "\n…（続きは省略）" : selected;
                sb.AppendLine("Selection:");
                sb.AppendLine(shown);
            }
        } else {
            sb.AppendLine("Editor file: none in front");
        }
        var others = _editorTabs
            .Where(open => open.PeekFilePath is { Length: > 0 } && !ReferenceEquals(open, _activeEditorTab))
            .Take(20)
            .Select(open => _workspace.ToDisplayPath(open.PeekFilePath!))
            .ToList();
        if (others.Count > 0) {
            sb.AppendLine("Other open files:");
            foreach (var other in others)
                sb.AppendLine($"- {other}");
        }
        return new LoomoRoomResult(true, sb.ToString().TrimEnd());
    }

    private void WriteReviewNoteForClaude() {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path) {
            ToastService.Info("指摘を書くファイルを前に出してください（カーソルの行に宛てます）");
            return;
        }
        // 届け先：このワークスペースの Claude のうち、タブのセッションが分かっているもの（承認待ちを先に並べた順）。
        var session = _paneActivity.AgentTabs()
            .Select(agent => _paneActivity.AgentSessionOf(agent.TabId)?.SessionId)
            .FirstOrDefault(id => id is { Length: > 0 });
        if (session is null) {
            ToastService.Info("Claude が動いているタブがありません。指摘は、動いている Claude の次の頼みごとに添えられます");
            return;
        }
        // エディタのカーソルは 0 始まり（LSP と同じ）。人に見せるのは 1 始まり。
        var line = tab.Control.Caret.Line + 1;
        var where = $"{_workspace.ToDisplayPath(path)}:{line}";
        var text = InputDialog.Prompt(this, "Claude への指摘",
            $"{where} の行に宛てる指摘です。送ると、動いている Claude が次の頼みごとでこれを読みます。", "",
            multiline: true);
        if (string.IsNullOrWhiteSpace(text))
            return;
        _claudeNotes.Add(session, new ClaudeNote(ClaudeNote.KindReview, $"{where} について：{text.Trim()}", DateTimeOffset.Now));
        ToastService.Info($"指摘を書きました（{where}）。Claude が次の頼みごとで読みます");
    }
}
