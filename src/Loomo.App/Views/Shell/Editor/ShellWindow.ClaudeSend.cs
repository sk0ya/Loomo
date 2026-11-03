namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタの場所をターミナルの Claude Code へ送る（§24.3.1）。
/// 選択があればその行範囲、なければファイルを <c>@path#L10-24</c> としてプロンプトへ入力する（Enter は押さない）。
/// 項目は Claude Code が動いているタブ（§24.1.1 のタイトルの印で分かる）があるときだけ出す。</summary>
public partial class ShellWindow {
    /// <summary>「ペインへ送る」の先頭に入れる「Claude Code（…）」。Claude Code のタブが無ければ null。</summary>
    private MenuItem? BuildClaudeSendMenuItem(VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path)
            return null;
        if (ClaudeTerminalTab() is not { } tab)
            return null;
        int? start = null, end = null;
        if (control.HasSelection && control.SelectionAsLspRange() is { } selection) {
            (var s, var e) = ClaudeCodeReference.LinesOfSelection(
                selection.Start.Line, selection.End.Line, selection.End.Character);
            (start, end) = (s, e);
        }
        var item = new MenuItem {
            Header = $"Claude Code（{ClaudeCodeReference.Label(path, start, end)}）",
            ToolTip = "この場所を @参照 としてターミナルの Claude Code のプロンプトへ入力する（送信はしない）",
        };
        item.Click += (_, _) => SendReferenceToClaude(tab, path, start, end);
        return item;
    }
    /// <summary>Claude Code が動いているターミナルのタブ（前面のタブを先に）。無ければ null。</summary>
    private TerminalTab? ClaudeTerminalTab()
        => _paneActivity.AgentTabFor(_activeTerminalTab?.Id) is { } tabId ? _terminalTabs.FirstOrDefault(t => t.Id == tabId) : null;

    /// <summary>ページから読んだもの（PR・Issue の Markdown）を Claude Code へ渡す（§41）。本文をプロンプトに貼ると長くて
    /// 頼みごとが埋もれるので、一時フォルダーに書いて <c>@参照</c> で渡す——エディタの場所を送るのと同じ形（送信はしない）。</summary>
    private async Task HandToClaudeAsync(string fileName, string markdown) {
        if (ClaudeTerminalTab() is not { } tab) {
            _browserSupport.ReportStatus("Claude Code が動いているターミナルがありません。ターミナルで claude を起動してください。");
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "Loomo", "handoff");
        // 名前にはリポジトリが入る（owner/repo の / や プロジェクト名の記号はファイル名に使えない字に置き換える）。
        var invalid = Path.GetInvalidFileNameChars();
        var path = Path.Combine(folder, new string(fileName.Select(c => invalid.Contains(c) ? '-' : c).ToArray()));
        try {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, markdown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _browserSupport.ReportStatus($"渡すものを書けませんでした: {ex.Message}");
            return;
        }
        SendReferenceToClaude(tab, path, null, null);
    }

    private void SendReferenceToClaude(TerminalTab tab, string path, int? start, int? end) {
        if (_terminalTabs.All(t => t.Id != tab.Id))
            return;   // メニューを開いている間に閉じられた
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
        ActivateTerminalTab(tab.Id);
        tab.View.SendTerminalInput(ClaudeCodeReference.Format(path, tab.View.WorkingDirectory, start, end));
        FocusPane(PaneKind.Terminal);
    }
}
