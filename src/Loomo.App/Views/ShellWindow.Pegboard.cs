using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

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
    }

    /// <summary>「ブラウザのURLをピン」：ブラウザペインで表示中のページをカードにする。</summary>
    private void PinBrowserUrlToPegboard()
    {
        if (_activeBrowserTab?.View.Source?.ToString() is { Length: > 0 } url)
            _vm.Pegboard.AddContent(url, type: "url",
                title: _activeBrowserTab.View.CoreWebView2?.DocumentTitle);
    }

    /// <summary>カードの「開く」：url→ブラウザ新タブ / file→エディタ（フォルダはターミナル cd）/ text→エディタの仮想ドキュメント。</summary>
    private async Task OpenPegboardItemAsync(PegboardItemVm item)
    {
        switch (item.Type)
        {
            case "url":
                SetPaneVisible(PaneKind.Browser, true);
                var tab = await CreateBrowserTabAsync(item.Content);
                ActivateBrowserTab(tab.Id);
                break;

            case "file" when File.Exists(item.Content):
                SetPaneVisible(PaneKind.Editor, true);
                await OpenFileInNewEditorTabAsync(item.Content);
                break;

            case "file" when Directory.Exists(item.Content):
                SetPaneVisible(PaneKind.Terminal, true);
                _terminal.SetWorkingDirectory(item.Content);
                FocusPane(PaneKind.Terminal);
                break;

            default:
                // テキスト片（や消えたファイルパス）は読み流し用の仮想ドキュメントで開く。
                SetPaneVisible(PaneKind.Editor, true);
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
