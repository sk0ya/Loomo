using System.Globalization;
using System.IO;
using System.Text;
using sk0ya.Loomo.Core.Pty;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 閉じたターミナルの画面を書庫から読む（§24.72）。書くのは常駐ホストで（<see cref="TerminalArchive"/>）、ここは並べて開くだけ。
/// </summary>
public partial class ShellWindow {
    private const string TerminalArchiveCategory = "閉じた画面";

    /// <summary>右の欄に出す末尾の行数。全文を読むと 40 組 × 1MB になるので、ファイルの末尾だけを読む。</summary>
    private const int ArchivePreviewLines = 20;

    /// <summary>パレット「閉じたターミナルの画面を読む」。この部屋で閉じたものを先に、新しい順に並べる。</summary>
    private void OpenTerminalArchive() {
        var entries = TerminalArchive.List(TerminalArchive.DefaultDirectory);
        var workspaceId = _activeWorkspace?.Id;
        var now = DateTime.UtcNow;
        var items = entries
            .OrderBy(e => e.WorkspaceId is { } id && id == workspaceId ? 0 : 1)
            .ThenByDescending(e => e.ClosedUtc)
            .Select(entry => new PaletteCommand(TerminalArchiveCategory, TerminalArchive.DisplayTitle(entry),
                () => _ = OpenTerminalArchiveEntryAsync(entry)) {
                Badge = sk0ya.Loomo.Services.GitWorktreeCleanup.FormatAgo(entry.ClosedUtc, now),
                Detail = ArchiveDetail(entry),
            }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(TerminalArchiveCategory, "閉じた画面はまだありません", () => { }) {
                Detail = "常駐のターミナルのタブを閉じて（3 分の預かりが切れて）から、または Loomo を終了したあとに、"
                         + "その画面がここに残ります。" + Environment.NewLine
                         + $"置き場: {TerminalArchive.DefaultDirectory}（新しい {TerminalArchive.MaxEntries} 個まで）",
            });
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private static string ArchiveDetail(TerminalArchiveEntry entry) {
        var sb = new StringBuilder();
        if (entry.WorkingDirectory is { Length: > 0 } cwd)
            sb.AppendLine($"場所: {cwd}");
        sb.AppendLine($"閉じた時刻: {entry.ClosedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.Append($"{entry.LineCount:N0} 行");
        if (entry.HeadOmitted) sb.Append("（先頭を省略）");
        if (entry.MaskedSecrets > 0) sb.Append($"・秘密らしい文字列を {entry.MaskedSecrets} か所伏せました");
        sb.AppendLine().AppendLine();
        sb.AppendLine("── 末尾 ──");
        sb.AppendLine(ReadTail(entry.TextPath, ArchivePreviewLines));
        sb.AppendLine();
        sb.Append("Enter でエディタに開く（読むための写し・保存しても残さない）");
        return sb.ToString();
    }

    /// <summary>ファイルの末尾の何行か（末尾 16KB だけ読む）。</summary>
    private static string ReadTail(string path, int lines) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int window = 16 * 1024;
            if (stream.Length > window) stream.Seek(-window, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            var all = text.Split('\n');
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return "（読めませんでした）";
        }
    }

    private async Task OpenTerminalArchiveEntryAsync(TerminalArchiveEntry entry) {
        string text;
        try { text = await File.ReadAllTextAsync(entry.TextPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ToastService.Info($"閉じた画面を読めませんでした: {ex.Message}");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            FileName = TerminalArchive.DocumentName(entry),
            Content = text,
            OnSaved = _ => { },   // 読むための写し：保存しても書庫は書き換えない
        });
    }
}
