
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ペグボードペイン（設計書 §23.3）のシェル側配線。
/// アイテムの「開く」を種別に応じて各ペインへ振り分け、変更をワークスペーススナップショットへ保存する。
/// </summary>
public partial class ShellWindow
{
    private void InitializePegboard()
    {
        _vm.Pegboard.Changed += (_, _) => SaveActiveWorkspaceSnapshot();
        _vm.Pegboard.OpenRequested += async (_, item) => await OpenPegboardItemAsync(item);
        _vm.Pegboard.BrowserPinRequested += (_, _) => PinBrowserUrlToPegboard();
        _vm.Pegboard.EditorSelectionPinRequested += (_, _) => PinEditorSelectionToPegboard();
        _vm.Pegboard.SendToTerminalRequested += (_, item) => SendPegboardItemToTerminal(item);
        _vm.Pegboard.InsertToComposerRequested += (_, item) => InsertIntoComposer(item.Content);
    }

    // 「ブラウザのURLをピン」：ブラウザペインで表示中のページをカードにする。
    private void PinBrowserUrlToPegboard()
    {
        if (_activeBrowserTab?.View.Source?.ToString() is { Length: > 0 } url)
            _vm.Pegboard.AddContent(url, type: "url",
                title: _activeBrowserTab.View.CoreWebView2?.DocumentTitle);
    }

    // 「エディタの選択をピン」：アクティブエディタの選択テキストをカードにする（素材の流れ）。
    private void PinEditorSelectionToPegboard()
    {
        if (_activeEditorTab?.Control.SelectedText is { Length: > 0 } text)
            _vm.Pegboard.AddContent(text, type: "text");
    }

    // 「ターミナルへ送る」：単一行は可視ターミナルのプロンプトへ入力する（実行はしない＝ FolderTree の「ターミナルにセット」と同じ流儀）。複数行はプロンプトで暴発しないよう コンポーザへ流し、推敲してから Ctrl+Enter で実行してもらう。
    private void SendPegboardItemToTerminal(PegboardItemVm item)
    {
        var content = item.Content;
        if (content.Contains('\n'))
        {
            InsertIntoComposer(content);
            return;
        }

        SetPaneVisible(PaneKind.Terminal, true);
        // 空白を含むパスはそのまま使えるよう引用する（ファイル/フォルダのカードのみ）。
        var text = item.Type == "file" && content.IndexOf(' ') >= 0 ? $"\"{content}\"" : content;
        _activeTerminalTab?.View.SendTerminalInput(text);
        FocusPane(PaneKind.Terminal);
    }

    // カードの「開く」：url→ブラウザ新タブ / file→エディタ（フォルダはターミナル cd）/ text→エディタの仮想ドキュメント。
    private async Task OpenPegboardItemAsync(PegboardItemVm item)
    {
        switch (item.Type)
        {
            case "url":
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
                var tab = await CreateBrowserTabAsync(item.Content);
                ActivateBrowserTab(tab.Id);
                break;

            case "file" when File.Exists(item.Content):
                // OpenFileInNewEditorTabAsync が EnsureEditorPaneForOpenedFile で
                // （バイナリ判定込みで）前面化するので、ここでの明示表示は不要。
                await OpenFileInNewEditorTabAsync(item.Content);
                break;

            case "file" when Directory.Exists(item.Content):
                SetPaneVisible(PaneKind.Terminal, true);
                _terminal.SetWorkingDirectory(item.Content);
                FocusPane(PaneKind.Terminal);
                break;

            default:
                // テキスト片（や消えたファイルパス）は読み流し用の仮想ドキュメントで開く。
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
                await _editor.OpenDocumentAsync(new EditorDocument
                {
                    FileName = $"pegboard-{item.Snapshot.Id.ToString("N")[..8]}.txt",
                    Content = item.Content,
                    OnSaved = _ => { }, // 閲覧用：保存しても永続化はしない
                });
                break;
        }
    }
}
