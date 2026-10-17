namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタの場所をターミナルの Claude Code へ送る（§24.3.1）。
/// 選択があればその行範囲、なければファイルを <c>@path#L10-24</c> としてプロンプトへ入力する（Enter は押さない）。
/// 項目は Claude Code が動いているタブ（§24.1.1 のタイトルの印で分かる）があるときだけ出す。</summary>
public partial class ShellWindow {
    private void AddClaudeSendMenuItem(ContextMenu menu, VimEditorControl? control) {
        if (control?.FilePath is not { Length: > 0 } path)
            return;
        if (_paneActivity.AgentTabFor(_activeTerminalTab?.Id) is not { } tabId
            || _terminalTabs.FirstOrDefault(t => t.Id == tabId) is not { } tab)
            return;
        int? start = null, end = null;
        if (control.HasSelection && control.SelectionAsLspRange() is { } selection) {
            (var s, var e) = ClaudeCodeReference.LinesOfSelection(
                selection.Start.Line, selection.End.Line, selection.End.Character);
            (start, end) = (s, e);
        }
        var item = new MenuItem {
            Header = $"Claude へ送る（{ClaudeCodeReference.Label(path, start, end)}）",
            ToolTip = "この場所を @参照 としてターミナルの Claude Code のプロンプトへ入力する（送信はしない）",
        };
        item.Click += (_, _) => SendReferenceToClaude(tab, path, start, end);
        menu.Items.Add(item);
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
