using System;
using System.IO;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 軌跡（操作ログ）バーの配線。エディタのファイル活性化とブラウザ遷移を
/// <see cref="TrailViewModel"/> へ記録し、チップのクリックでその地点へ戻る
/// （ファイル＝タブ活性化＋カーソル復元、URL＝ブラウザペインを出してナビゲート）。
/// アイデア.md「Semantic Depth」構想の Thread Rail の種となる MVP。</summary>
public partial class ShellWindow
{
    /// <summary>true の間は軌跡へ記録しない。ワークスペース切替・復元による機械的なタブ活性化と、
    /// 軌跡からの「戻る」自体（戻った先を新しい地点として積まない）で立てる。</summary>
    private bool _trailSuppressed;

    private void InitializeTrail()
    {
        _vm.Trail.JumpRequested += OnTrailJumpRequested;
        // 追記されたら右端（最新）が見えるよう追従スクロールする。
        _vm.Trail.Entries.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => TrailScroll.ScrollToRightEnd()), DispatcherPriority.Loaded);
    }

    /// <summary>エディタタブの活性化を軌跡へ記録する（無題・仮想ドキュメントは対象外）。</summary>
    private void RecordTrailEditorTab(EditorTab tab)
    {
        if (_trailSuppressed)
            return;

        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;

        RefreshLatestTrailFilePosition();

        var line = -1;
        var column = -1;
        if (tab.IsRealized)
        {
            line = tab.Control.Caret.Line;
            column = tab.Control.Caret.Column;
        }
        _vm.Trail.RecordFile(path, line, column);
    }

    /// <summary>新しい地点を積む直前に、最新エントリ（＝いま離れるファイル）のカーソル位置を
    /// タブの現在値で上書きする。これで「戻る」が到着時でなく離脱時の場所になる。</summary>
    private void RefreshLatestTrailFilePosition()
    {
        if (_vm.Trail.LatestEntry is not { Kind: TrailEntryKind.File } latest)
            return;

        var tab = _editorTabs.FirstOrDefault(t => t.IsRealized
            && string.Equals(t.PeekFilePath, latest.Target, StringComparison.OrdinalIgnoreCase));
        if (tab is not null)
            _vm.Trail.UpdateFilePosition(latest, tab.Control.Caret.Line, tab.Control.Caret.Column);
    }

    /// <summary>ブラウザ遷移を軌跡へ記録する。既定ページ（新規タブの初期表示）と about: は対象外。</summary>
    private void RecordTrailBrowser(string? url, string? title)
    {
        if (_trailSuppressed || string.IsNullOrWhiteSpace(url))
            return;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, DefaultBrowserUrl, StringComparison.OrdinalIgnoreCase))
            return;

        _vm.Trail.RecordBrowser(url, title);
    }

    private async void OnTrailJumpRequested(object? sender, TrailEntryViewModel entry)
    {
        // 戻る操作で辿った活性化・ナビゲートは新しい地点として記録しない。
        var saved = _trailSuppressed;
        _trailSuppressed = true;
        try
        {
            switch (entry.Kind)
            {
                case TrailEntryKind.File:
                    if (!File.Exists(entry.Target))
                        return;   // 消えたファイルはそっと何もしない（ブランチ切替等で戻ることもある）
                    await OpenFileInNewEditorTabAsync(entry.Target);
                    FocusPane(PaneKind.Editor);
                    if (entry.Line >= 0)
                        _activeEditorTab?.Control.NavigateTo(entry.Line, Math.Max(0, entry.Column));
                    break;

                case TrailEntryKind.Browser:
                    EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
                    FocusPane(PaneKind.Browser);
                    NavigateBrowser(entry.Target);
                    break;
            }
        }
        finally
        {
            _trailSuppressed = saved;
        }
    }
}
