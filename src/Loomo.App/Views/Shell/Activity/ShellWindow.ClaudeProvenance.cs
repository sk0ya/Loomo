using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: 出自（§43）。コミットしていない行の blame（行末の注記・<c>:Gblame</c> の欄）に、その行を書いた
/// Claude Code の頼みごとを出す。行から頼みごとへ（全文・会話の再開）、頼みごとから「それで変わった場所」へ。
/// 索引（<see cref="ClaudeProvenanceIndex"/>）は記録を裏で読む——ここ（UI スレッド）では、すでに読んだものを引くだけ。
/// </summary>
public partial class ShellWindow {
    private ClaudeProvenanceIndex? _claudeProvenance;
    private ClaudeProvenanceIndex ClaudeProvenance => _claudeProvenance ??= new ClaudeProvenanceIndex(_workspace);

    /// <summary>blame の欄で Claude の行を押した：頼みごとを通知欄に出す（コミットの行なら差分が開く場所）。</summary>
    private void ShowClaudeBlameRequest(Editor.Controls.Git.EditorBlameLine blame) {
        if (ClaudeProvenance.FromHandle(blame.OriginalLine) is not { } write)
            return;
        ToastService.Info($"この行を書いた頼みごと（{blame.Date}）\n{ClaudeTranscriptParser.Shorten(write.Prompt, 400)}");
    }

    private void AddClaudeBlameMenuItems(ContextMenu menu, VimEditorControl control, Editor.Controls.Git.EditorBlameLine blame) {
        if (ClaudeProvenance.FromHandle(blame.OriginalLine) is { } write) {
            var places = new MenuItem { Header = "この頼みごとで変わった場所…" };
            places.Click += (_, _) => OpenClaudeRequestPlaces(write);
            menu.Items.Add(places);
            var copy = new MenuItem { Header = "頼みごとの全文をコピー" };
            copy.Click += (_, _) => {
                try { Clipboard.SetText(write.Prompt); } catch { /* クリップボード占有中は無視 */ }
            };
            menu.Items.Add(copy);
            // セッション ID は記録のファイル名から来る文字列なので、シェルに打つ前に形を確かめる（UUID の文字だけ）。
            if (write.SessionId.Length is > 0 and <= 64 && write.SessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) {
                var resume = new MenuItem { Header = "この会話を再開（新しいタブで claude --resume）" };
                resume.Click += (_, _) => ResumeClaudeSession(write.SessionId);
                menu.Items.Add(resume);
            }
            menu.Items.Add(new Separator());
        }
        var close = new MenuItem { Header = "Blame を閉じる", InputGestureText = ":Gblame" };
        close.Click += (_, _) => control.ExecuteCommand("Gblame");
        menu.Items.Add(close);
    }

    /// <summary>頼みごと → それで変わった場所。ファイルごとに 1 行、選ぶとその書き込みがいまある行へ行く
    /// （行は中身で探す。もう残っていなければファイルの先頭）。</summary>
    private async void OpenClaudeRequestPlaces(ClaudeWrite origin) {
        var writes = ClaudeProvenance.WritesOfRequest(origin);
        var places = await Task.Run(() => writes
            .GroupBy(write => write.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Path: group.Key, Edits: group.Count(), Added: group.Sum(w => w.Lines.Count),
                Line: ClaudeRequestPlaces.FirstLineOf(group.Key, group)))
            .ToList());
        var items = places.Select(place => new PaletteCommand("変わった場所",
            _workspace.ToDisplayPath(place.Path),
            () => _ = OpenPathInEditorAsync(place.Path, place.Line ?? 1, 1)) {
            Badge = $"+{place.Added}",
            Detail = string.Join(Environment.NewLine, new[] {
                $"頼みごと: {origin.PromptHead}",
                $"書き込み {place.Edits} 回・足した行 {place.Added}",
                place.Line is { } line ? $"いまの {line} 行目" : "この頼みごとが足した行は、もうファイルに残っていません",
            }),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand("変わった場所", "この頼みごとの書き込みは見つかりませんでした", () => { }));
        OpenCommandPalette(PaletteMode.Command, items);
    }
}

/// <summary>頼みごとの書き込みが、いまのファイルのどこにあるか（中身で探す）。</summary>
internal static class ClaudeRequestPlaces {
    /// <summary>書き込みが足した行のうち、いまのファイルで最初に見つかる行（1 始まり）。読めない・残っていなければ null。</summary>
    public static int? FirstLineOf(string path, IEnumerable<ClaudeWrite> writes) {
        string[] current;
        try { current = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return FirstLineOf(current, writes);
    }

    public static int? FirstLineOf(IReadOnlyList<string> current, IEnumerable<ClaudeWrite> writes) {
        var added = new HashSet<string>(
            writes.SelectMany(write => write.Lines).Where(line => line.Trim().Length >= 6), StringComparer.Ordinal);
        for (var i = 0; i < current.Count; i++) {
            if (added.Contains(current[i].TrimEnd()))
                return i + 1;
        }
        return null;
    }
}
