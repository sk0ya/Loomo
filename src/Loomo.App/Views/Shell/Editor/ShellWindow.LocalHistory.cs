namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ローカル履歴（Rider の Local History・設計書 §36）。記録は開いた時点と保存のたび
/// （<see cref="AfterEditorFileLoaded"/>・<see cref="EditorService.FileSaved"/>）、見るのはパレットから。
/// 選んだ版はファイルを書き換えずに差分ペインで「版 ↔ いま」を見せ、戻すときだけエディタの本文を
/// 1 回の Undo で戻せる編集として置き換える（保存は人がする）。
/// </summary>
public partial class ShellWindow
{
    private const string LocalHistoryCategory = "ローカル履歴";

    private readonly LocalHistoryStore _localHistory = new(LocalHistoryStore.DefaultRoot());

    private void RecordSavedToLocalHistory(VimEditorControl control)
    {
        if (control.IsVirtualDocument || control.FilePath is not { Length: > 0 } path)
            return;
        // 保存は打鍵の経路ではないので、本文の写しはここで取ってよい。圧縮と書き込みは背景。
        _localHistory.RecordContent(path, control.Text, LocalHistoryKind.Saved);
    }

    /// <summary>「この時点に名前を付けて残す…」：いまの本文（未保存でも）を名前付きの版にする（§36.8）。
    /// 名前付きの版は 14 日・200 版の刈り込みから外れ、一覧では名前で出る。</summary>
    private void LabelLocalHistoryVersion()
    {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path
            || tab.Control.IsVirtualDocument)
        {
            ShowRefactorStatus("ローカル履歴：ファイルを開いているエディタがありません。");
            return;
        }
        var label = InputDialog.Prompt(this, "この時点に名前を付けて残す",
            $"{Path.GetFileName(path)} のいまの本文を、名前付きの版としてローカル履歴に残します（期限で消えません）。", "");
        if (string.IsNullOrWhiteSpace(label)) return;
        _localHistory.RecordLabeled(path, tab.Control.Text, label);
        tab.Control.ShowStatusMessage($"ローカル履歴に「{LocalHistoryStore.NormalizeLabel(label)}」として残しました");
    }

    /// <summary>「ローカル履歴を表示」：いまのファイルの版を並べ、選ぶと差分ペインで「版 ↔ いま」。</summary>
    private void ShowLocalHistory() => OpenLocalHistoryList(restore: false);

    /// <summary>「ローカル履歴から戻す」：選んだ版へエディタの本文を戻す（1 回の Undo で取り消せる）。</summary>
    private void RestoreFromLocalHistory() => OpenLocalHistoryList(restore: true);

    private void OpenLocalHistoryList(bool restore)
    {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path)
        {
            ShowRefactorStatus("ローカル履歴：ファイルを開いているエディタがありません。");
            return;
        }

        var control = tab.Control;
        var current = control.Text;
        var now = DateTime.UtcNow;
        var items = _localHistory.GetEntries(path).Select(entry =>
        {
            Action run = restore
                ? () => RestoreLocalHistoryEntry(control, entry)
                : () => ShowLocalHistoryDiff(entry, current);
            return new PaletteCommand(LocalHistoryPresentation.RelativeTime(entry.TimestampUtc, now),
                $"{entry.KindLabel}  {Path.GetFileName(path)}", run)
            {
                Badge = entry.KindLabel,
                Detail = (restore ? "Enter でエディタの本文をこの版に戻す（保存はしない・Ctrl+Z で取り消せる）" :
                    "Enter で「この版 ↔ いまの本文」を差分ペインで開く")
                    + Environment.NewLine + LocalHistoryPresentation.VersionTitle(entry),
            };
        }).ToList();

        if (items.Count == 0)
            items.Add(new PaletteCommand(LocalHistoryCategory, "このファイルの履歴はまだありません", () => { })
            {
                Detail = "エディタで開いた時点と、保存のたびに残ります（14 日・1 ファイル 200 版まで）。" +
                         "「この時点に名前を付けて残す…」で残した版は期限で消えません。",
            });
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private void ShowLocalHistoryDiff(LocalHistoryEntry entry, string current)
    {
        string version;
        try { version = _localHistory.ReadContent(entry); }
        catch (Exception ex) { ShowRefactorStatus($"ローカル履歴を読めませんでした: {ex.Message}"); return; }
        ShowDiff(new DiffOpenTarget.Comparison(new DiffComparison(
            LocalHistoryPresentation.VersionTitle(entry), version,
            "いまの本文", current, entry.FilePath)));
    }

    private void RestoreLocalHistoryEntry(VimEditorControl control, LocalHistoryEntry entry)
    {
        string version;
        try { version = _localHistory.ReadContent(entry); }
        catch (Exception ex) { ShowRefactorStatus($"ローカル履歴を読めませんでした: {ex.Message}"); return; }

        var current = control.Text;
        if (string.Equals(current.Replace("\r\n", "\n"), version.Replace("\r\n", "\n"), StringComparison.Ordinal))
        {
            ShowRefactorStatus("いまの本文はこの版と同じです。");
            return;
        }

        // 戻す前の本文も 1 版として残す——戻したこと自体を、履歴からまた戻せるように。
        _localHistory.RecordContent(control.FilePath, current, LocalHistoryKind.BeforeRestore);
        var edit = new LspTextEdit(LocalHistoryPresentation.WholeDocumentRange(current), version.Replace("\r\n", "\n"));
        if (!control.TryApplyLspTextEdits([edit], expectedVersion: null, out var error))
        {
            ShowRefactorStatus($"ローカル履歴から戻せませんでした: {error}");
            return;
        }
        control.ShowStatusMessage($"{LocalHistoryPresentation.VersionTitle(entry)} の版に戻しました（未保存・Ctrl+Z で取り消し）");
    }

    /// <summary>「最近の変更（ローカル履歴）」：ファイル横断で新しい順。選ぶと「版 ↔ いまのディスク」。</summary>
    private void ShowRecentLocalHistory()
    {
        var now = DateTime.UtcNow;
        var items = _localHistory.GetRecent().Select(entry => new PaletteCommand(
            LocalHistoryPresentation.RelativeTime(entry.TimestampUtc, now),
            _workspace.ToDisplayPath(entry.FilePath),
            () => ShowLocalHistoryDiff(entry, File.Exists(entry.FilePath) ? SafeReadAllText(entry.FilePath) : ""))
        {
            Badge = File.Exists(entry.FilePath) ? entry.KindLabel : "削除済み",
            Detail = "Enter で「この版 ↔ いまのディスク」を差分ペインで開く" + Environment.NewLine +
                     LocalHistoryPresentation.VersionTitle(entry),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(LocalHistoryCategory, "履歴はまだありません", () => { }));
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>「削除したファイルを復元（ローカル履歴）」：今は無いファイルの最後の版をディスクへ書き戻して開く。</summary>
    private void RestoreDeletedFileFromLocalHistory()
    {
        var now = DateTime.UtcNow;
        var items = _localHistory.GetDeletedFiles().Select(entry => new PaletteCommand(
            LocalHistoryPresentation.RelativeTime(entry.TimestampUtc, now),
            _workspace.ToDisplayPath(entry.FilePath),
            () => _ = RestoreDeletedFileAsync(entry))
        {
            Badge = "削除済み",
            Detail = "Enter で最後の版をこの場所へ書き戻して開く" + Environment.NewLine +
                     LocalHistoryPresentation.VersionTitle(entry),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(LocalHistoryCategory, "復元できる削除済みファイルはありません", () => { }));
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private async Task RestoreDeletedFileAsync(LocalHistoryEntry entry)
    {
        if (File.Exists(entry.FilePath))
        {
            ShowRefactorStatus($"{Path.GetFileName(entry.FilePath)} は既にあります。上書きはしません。");
            return;
        }
        try
        {
            var text = _localHistory.ReadContent(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(entry.FilePath)!);
            await File.WriteAllTextAsync(entry.FilePath, text);
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"復元できませんでした: {ex.Message}");
            return;
        }
        await OpenFileInNewEditorTabAsync(entry.FilePath);
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return ""; }
    }
}
