using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>検索結果の 1 行の書き換え（§24.86）。<see cref="Original"/> は検索したときの行（末尾の空白を落としたもの）、行番号は 1 始まり。</summary>
internal sealed record SearchLineEdit(int Line, string Original, string Edited);

/// <summary>1 ファイルへの書き戻しの結果。<see cref="Stale"/> は検索した後に元の行が変わっていて飛ばした数。</summary>
internal sealed record SearchLineEditResult(int Applied, int Stale, bool WriteFailed = false);

/// <summary>
/// 検索結果の行を、それぞれの元ファイルの該当行へ書き戻す（§24.86・Emacs 31.1 の grep-edit-mode）。
/// 書く前に<b>元の行が検索したときのままか</b>を確かめ、変わっていればその行は飛ばす（その後の編集を検索結果の古い写しで上書きしない）。
/// 行末の空白は検索結果では落としているので、元の行にあった分は書き換えた行の後ろへ戻す。改行コード・文字コード・BOM は元のまま
/// （<see cref="TextFileEncoding"/>。置換と同じ）。
/// </summary>
internal static class SearchLineEdits
{
    public static SearchLineEditResult ApplyToFile(string fullPath, IReadOnlyList<SearchLineEdit> edits)
    {
        if (edits.Count == 0 || TextFileEncoding.ReadFile(fullPath) is not { } decoded)
            return new(0, edits.Count, WriteFailed: edits.Count > 0);
        var (text, applied, stale) = Apply(decoded.Text, edits);
        if (applied == 0)
            return new(0, stale);
        return TextFileEncoding.TryWriteFile(fullPath, decoded, text)
            ? new(applied, stale)
            : new(0, edits.Count, WriteFailed: true);
    }

    /// <summary>本文 <paramref name="text"/> へ書き換えを当てる（ファイルに触れない部分・テスト用）。</summary>
    internal static (string Text, int Applied, int Stale) Apply(string text, IReadOnlyList<SearchLineEdit> edits)
    {
        var lines = SplitKeepingEndings(text);
        var applied = 0;
        var stale = 0;
        foreach (var edit in edits.GroupBy(e => e.Line).Select(g => g.Last()))
        {
            var index = edit.Line - 1;
            if (index < 0 || index >= lines.Count)
            {
                stale++;
                continue;
            }
            var (content, ending) = lines[index];
            var trimmed = content.TrimEnd();
            if (!string.Equals(trimmed, edit.Original, StringComparison.Ordinal))
            {
                stale++;
                continue;
            }
            if (string.Equals(edit.Edited, edit.Original, StringComparison.Ordinal))
                continue;
            lines[index] = (edit.Edited + content[trimmed.Length..], ending);
            applied++;
        }
        return (string.Concat(lines.Select(l => l.Content + l.Ending)), applied, stale);
    }

    private static List<(string Content, string Ending)> SplitKeepingEndings(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add((text[start..end], text[end..(i + 1)]));
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add((text[start..], ""));
        return lines;
    }
}
