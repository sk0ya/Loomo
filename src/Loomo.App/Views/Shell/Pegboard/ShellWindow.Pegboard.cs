namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペグボードペイン（設計書 §23.3）のシェル側配線。 アイテムの「開く」を種別に応じて各ペインへ振り分け、変更をワークスペーススナップショットへ保存する。</summary>
public partial class ShellWindow {
    private void InitializePegboard() {
        _vm.Pegboard.Changed += (_, _) => SaveActiveWorkspaceSnapshot();
        _vm.Pegboard.OpenRequested += async (_, item) => await OpenPegboardItemAsync(item);
        _vm.Pegboard.OpenSourceRequested += async (_, item) => {
            if (item.HasTerminalSource)
                await ReturnToTerminalSourceAsync(item);   // ターミナルの出典（§24.26）
            else if (item.SourceLink is { } link)
                await OpenUrlInNewBrowserTabAsync(link);
        };
        _vm.Pegboard.BrowserPinRequested += (_, _) => PinBrowserUrlToPegboard();
        _vm.Pegboard.EditorSelectionPinRequested += (_, _) => PinEditorSelectionToPegboard();
        _vm.Pegboard.SendToTerminalRequested += (_, item) => SendPegboardItemToTerminal(item);
        _vm.Pegboard.SendToComposerRequested += (_, item) => InsertIntoComposer(item.Content);
    }
    private void PinBrowserUrlToPegboard() {
        if (_activeBrowserTab is { } tab && BrowserUrlOf(tab) is { Length: > 0 } url)
            _vm.Pegboard.AddContent(url, type: "url", title: tab.CurrentTitle);
    }
    private void PinEditorSelectionToPegboard() {
        if (_activeEditorTab?.Control.SelectedText is { Length: > 0 } text)
            _vm.Pegboard.AddContent(text, type: "text");
    }
    private void SendPegboardItemToTerminal(PegboardItemVm item) {
        var content = item.Content;
        if (content.Contains('\n')) {
            InsertIntoComposer(content);
            return;
        }
        SetPaneVisible(PaneKind.Terminal, true);
        var text = item.Type == "file" && content.IndexOf(' ') >= 0 ? $"\"{content}\"" : content;
        _activeTerminalTab?.View.SendTerminalInput(text);
        FocusPane(PaneKind.Terminal);
    }
    private async Task OpenUrlInNewBrowserTabAsync(string url) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        var tab = await CreateBrowserTabAsync(url);
        ActivateBrowserTab(tab.Id);
    }
    private async Task OpenPegboardItemAsync(PegboardItemVm item) {
        switch (item.Type) {
            case "url":
                await OpenUrlInNewBrowserTabAsync(item.Content);
                break;
            case "file" when File.Exists(item.Content):
                await OpenFileInNewEditorTabAsync(item.Content);
                break;
            case "file" when Directory.Exists(item.Content):
                SetPaneVisible(PaneKind.Terminal, true);
                _terminal.SetWorkingDirectory(item.Content);
                FocusPane(PaneKind.Terminal);
                break;
            default:
                await OpenPegboardTextInEditorAsync(item);
                break;
        }
    }
    private async Task OpenPegboardTextInEditorAsync(PegboardItemVm item) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            // ターミナルから残したものは §24.21 の「出力-先頭語-時刻.log」に揃える（どのコマンドのものか分かる）。
            FileName = item.HasTerminalSource
                ? TerminalPegboardMaterial.DocumentName(item.Snapshot.TerminalSource!)
                : $"pegboard-{item.Snapshot.Id.ToString("N")[..8]}.txt",
            Content = item.Content,
            OnSaved = _ => { }, // 閲覧用：保存しても永続化はしない
        });
    }
}
