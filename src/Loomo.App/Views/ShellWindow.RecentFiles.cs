using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 最近開いたファイル（MRU）と、開いているエディタタブの最近使用順（Ctrl+Tab 切替）。
/// - <see cref="_recentFiles"/> はセッションをまたいで永続化する開いたファイルの履歴（コマンドパレットの候補）。
/// - <see cref="_editorMru"/> は現在開いているエディタタブの使用順（新しい→古い）で、Ctrl+Tab で直前タブへ戻る。
/// </summary>
public partial class ShellWindow
{
    private readonly RecentFilesStore _recentFiles = new();

    /// <summary>開いているエディタタブの最近アクティブ順（先頭＝今アクティブ）。閉じたタブは取り除く。</summary>
    private readonly List<Guid> _editorMru = new();

    /// <summary>実ファイルを開いた／プレビューしたときに永続 MRU 履歴へ記録する（仮想ドキュメントは除外）。</summary>
    private void RecordRecentFile(string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            _recentFiles.Add(path);
    }

    /// <summary>エディタタブがアクティブになったとき、使用順 MRU の先頭へ繰り上げる。</summary>
    private void RecordEditorMru(Guid id)
    {
        _editorMru.Remove(id);
        _editorMru.Insert(0, id);
    }

    /// <summary>エディタタブを閉じたとき、使用順 MRU から取り除く。</summary>
    private void ForgetEditorMru(Guid id) => _editorMru.Remove(id);

    /// <summary>Ctrl+Tab：直前に使っていたエディタタブへ切り替える（Alt+Tab の単発と同じ「直前へ」挙動）。
    /// 開いているタブだけを対象に、現在アクティブの次に新しいものへ移る。</summary>
    private void SwitchToPreviousEditorTab()
    {
        // 既に閉じられたタブを掃除しつつ、現在開いている順序付きの一覧にする。
        _editorMru.RemoveAll(id => _editorTabs.All(t => t.Id != id));
        if (_editorMru.Count < 2)
            return;

        var target = _editorMru[1]; // [0]=現在アクティブ、[1]=直前
        SetPaneVisible(PaneKind.Editor, true);
        ActivateEditorTab(target);  // 内部で RecordEditorMru され、[0] と [1] が入れ替わる
    }

    /// <summary>コマンドパレットへ「最近開いたファイル」候補を積む（存在するファイルのみ・新しい順）。</summary>
    private void AddRecentFileCommands(List<PaletteCommand> list)
    {
        const int max = 20;
        var added = 0;
        foreach (var path in _recentFiles.Entries)
        {
            if (added >= max)
                break;
            if (!File.Exists(path))
                continue;
            var full = path;
            list.Add(new("最近", RecentRelativeLabel(full), () => _ = OpenAndNavigateAsync(full, 0))
            { PreviewPath = full });
            added++;
        }
    }

    /// <summary>最近ファイルの表示名。ワークスペースルート配下なら相対パス、外なら短縮フルパス。</summary>
    private string RecentRelativeLabel(string fullPath)
    {
        var root = _workspace.RootPath;
        if (!string.IsNullOrEmpty(root))
        {
            try
            {
                var rel = Path.GetRelativePath(root, fullPath);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                    return rel.Replace('\\', '/');
            }
            catch { /* 相対化できなければフルパス表示にフォールバック */ }
        }
        return fullPath;
    }
}
