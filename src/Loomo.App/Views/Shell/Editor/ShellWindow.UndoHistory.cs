using Editor.Core.Buffer;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 取り消しの履歴を、タブを閉じても・Loomo を閉じても残す（Vim の 'undofile'・§24.96）。
/// 書き出すのはエディタがファイルを手放すとき——タブを閉じる・同じタブへ別のファイルを読む・ウィンドウを閉じる——で、
/// 打鍵や保存の道には置かない。戻すのはファイルを読み込んだあと（復元を含む）。本文が書き出したときと違えば
/// エディタが断るので、外で書き換わったファイルに古い履歴は付かない。
/// </summary>
public partial class ShellWindow
{
    private readonly UndoHistoryStore _undoHistory = new(UndoHistoryStore.DefaultRoot());

    private static readonly UndoExportOptions UndoExport = new(MaxCharacters: UndoHistoryStore.MaxCharacters);

    /// <summary>このエディタが持っている取り消しの履歴を書き出す（取り消すものが無ければ前の分をそのまま残す）。</summary>
    private void SaveUndoHistory(VimEditorControl control)
    {
        if (!_settings.Editor.PersistUndoHistory || control.FilePath is not { Length: > 0 } path || !Path.IsPathRooted(path))
            return;
        if (MetadataSourceFiles.IsMetadataAsSource(path))
            return;   // ライブラリの定義（読み取り専用の写し）は残さない
        try
        {
            if (control.ExportUndoHistory(UndoHistoryStore.DocumentId(path), UndoExport) is { } json)
                _undoHistory.Save(path, json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"[Undo] 書き出せませんでした: {ex.Message}");
        }
    }

    /// <summary>読み込んだファイルに、前に書き出した取り消しの履歴を戻す。読むのは背景、入れるのは UI スレッド。
    /// 待っている間に別のファイルへ替わった・打ち始めた（本文が変わった）なら、エディタが断るか、ここで入れない。</summary>
    private async void RestoreUndoHistory(VimEditorControl control)
    {
        if (!_settings.Editor.PersistUndoHistory || control.FilePath is not { Length: > 0 } path || !Path.IsPathRooted(path))
            return;
        var json = await Task.Run(() => _undoHistory.Load(path));
        if (json is null || !string.Equals(control.FilePath, path, StringComparison.OrdinalIgnoreCase))
            return;
        var result = control.ImportUndoHistory(json, UndoHistoryStore.DocumentId(path));
        if (!result.Success)
            System.Diagnostics.Debug.WriteLine($"[Undo] 戻しませんでした（{path}）: {result.Error}");
    }

    /// <summary>ウィンドウを閉じる：開いている全部の部屋のエディタの履歴を書き出してから、書き込みを書き切る。</summary>
    private void SaveAllUndoHistories()
    {
        var controls = _editorTabs.Concat(_editorWorkspaces.Values.SelectMany(w => w.Tabs))
            .Where(tab => tab.IsRealized)
            .Select(tab => tab.Control)
            .Distinct();
        foreach (var control in controls)
            SaveUndoHistory(control);
        _undoHistory.Dispose();
    }
}
