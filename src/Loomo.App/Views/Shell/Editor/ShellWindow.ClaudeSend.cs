using sk0ya.Loomo.App.Services;

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
    /// <summary>Alt+K：エディタで見ている場所（選択があれば行範囲）を <c>@参照</c> で Claude Code の入力欄へ入れる。
    /// 送信はしない。Claude が動いているタブが無ければ知らせるだけ。</summary>
    private void SendEditorLocationToClaude() => SendLocationOfToClaude(_activeEditorTab?.Control);

    private void SendLocationOfToClaude(VimEditorControl? control) {
        if (control is null)
            return;
        // 知らせはエディタの下の状態行へ出す（トーストは一瞬で消え、押したのに何も起きないように見える）。
        if (control.FilePath is not { Length: > 0 } path) {
            control.ShowStatusMessage("Alt+K: 場所を渡すファイルがありません（先にファイルを保存してください）。");
            return;
        }
        if (ClaudeTerminalTab() is not { } tab) {
            control.ShowStatusMessage("Alt+K: Claude Code が動いているタブが見つかりません。ターミナルの claude を前に出してから押してください。");
            return;
        }
        int? start = null, end = null;
        if (control.HasSelection && control.SelectionAsLspRange() is { } selection) {
            (var s, var e) = ClaudeCodeReference.LinesOfSelection(
                selection.Start.Line, selection.End.Line, selection.End.Character);
            (start, end) = (s, e);
        }
        SendReferenceToClaude(tab, path, start, end);
    }

    /// <summary>ターミナルの選んだ文字を Claude Code へ渡す（右クリック「ペインへ送る ▸ Claude Code」）。
    /// 長いログが頼みごとを埋めないよう、一時フォルダーに書いて <c>@参照</c> で渡す（ブラウザの PR と同じ形）。</summary>
    private MenuItem? BuildTerminalClaudeSendItem(string selectedText, bool hasSelection) {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText) || ClaudeTerminalTab() is null)
            return null;
        var lines = selectedText.Split('\n').Length;
        var item = new MenuItem {
            Header = $"Claude Code（選んだ {lines} 行）",
            ToolTip = "選んだ文字を一時ファイルに書き、@参照 としてターミナルの Claude Code のプロンプトへ入力する（送信はしない）",
        };
        item.Click += (_, _) => _ = HandToClaudeAsync(
            "terminal-selection.txt", selectedText, _terminalSupport.ReportNotice);
        return item;
    }

    /// <summary>ターミナルサポートのコマンドの出力を Claude Code へ渡す（一時ファイル＋<c>@参照</c>）。</summary>
    private Task HandTerminalOutputToClaudeAsync(TerminalCommandOutput output)
        => HandToClaudeAsync(TerminalCommandOutputs.DocumentName(output), output.Text, _terminalSupport.ReportNotice);

    /// <summary>Claude Code が動いているターミナルのタブ（前面のタブを先に）。無ければ null。</summary>
    private TerminalTab? ClaudeTerminalTab()
        => _paneActivity.AgentTabFor(_activeTerminalTab?.Id) is { } tabId ? _terminalTabs.FirstOrDefault(t => t.Id == tabId) : null;

    /// <summary>ページから読んだもの（PR・Issue の Markdown）を Claude Code へ渡す（§41）。本文をプロンプトに貼ると長くて
    /// 頼みごとが埋もれるので、一時フォルダーに書いて <c>@参照</c> で渡す——エディタの場所を送るのと同じ形（送信はしない）。</summary>
    /// <param name="report">知らせを出す場所。ターミナル由来なら<c>_terminalSupport.ReportNotice</c>（既定はブラウザの状態欄）。</param>
    private async Task HandToClaudeAsync(string fileName, string markdown, Action<string>? report = null) {
        report ??= _browserSupport.ReportStatus;
        if (ClaudeTerminalTab() is not { } tab) {
            report("Claude Code が動いているターミナルがありません。ターミナルで claude を起動してください。");
            return;
        }
        var folder = ClaudeHandoffFiles.Folder;
        // 同じ秒に二度渡されても上書きしないよう、名前に短い一意の印を入れる。
        // 名前にはリポジトリが入る（owner/repo の / や プロジェクト名の記号はファイル名に使えない字に置き換える）。
        var invalid = Path.GetInvalidFileNameChars();
        var stamp = $"{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var named = $"{Path.GetFileNameWithoutExtension(fileName)}-{stamp}{Path.GetExtension(fileName)}";
        var path = Path.Combine(folder, new string(named.Select(c => invalid.Contains(c) ? '-' : c).ToArray()));
        try {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, markdown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            report($"渡すものを書けませんでした: {ex.Message}");
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
