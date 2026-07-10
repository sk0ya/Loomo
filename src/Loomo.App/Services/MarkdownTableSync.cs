using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown テーブルの列揃え（区切り行 <c>:---: </c> の記法）。</summary>
public enum MarkdownColumnAlignment
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>
/// エディタ本文中の GitHub 風 Markdown テーブル（ヘッダ行＋区切り行 <c>|---|---|</c>＋本文行）を
/// 抽出・再生成する純ロジック。VGrid グリッドで編集するために本文↔セル行列を相互変換する。
/// </summary>
/// <param name="StartLine">テーブル先頭（ヘッダ）行の 0 始まり行番号。</param>
/// <param name="EndLine">テーブル末尾行の 0 始まり行番号（この行を含む）。</param>
/// <param name="Rows">区切り行を除いた行列（先頭がヘッダ、以降が本文）。</param>
/// <param name="Alignments">列ごとの揃え（区切り行から解釈）。</param>
public sealed record MarkdownTableRegion(
    int StartLine,
    int EndLine,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<MarkdownColumnAlignment> Alignments);

/// <summary>Markdown テーブルの検出・パース・再生成（<see cref="MarkdownTableGridWindow"/> の純ロジック部分）。</summary>
public static class MarkdownTableSync
{
    // 区切り行のセル（例: ---, :---, ---:, :---:）。ダッシュ1本以上を許す。
    private static readonly Regex SeparatorCell = new(@"^:?-+:?$", RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="caretLine"/>（0 始まり）を含む Markdown テーブルを探す。見つからなければ false。
    /// テーブルは「'|' を含む非空行が連続し、その中に区切り行がある」ブロックとして判定し、
    /// 区切り行の直前をヘッダ、以降を本文とする（先頭に別段落が紛れていても区切り行を軸に切り出す）。
    /// </summary>
    public static bool TryFindTableAt(string[] lines, int caretLine, out MarkdownTableRegion region)
    {
        region = null!;
        if (lines.Length == 0 || caretLine < 0 || caretLine >= lines.Length)
            return false;
        if (!IsTableLine(lines[caretLine]))
            return false;

        // カーソル行を含む連続した「テーブルらしい行」の範囲を上下に広げる。
        int start = caretLine;
        while (start > 0 && IsTableLine(lines[start - 1]))
            start--;
        int end = caretLine;
        while (end < lines.Length - 1 && IsTableLine(lines[end + 1]))
            end++;

        // ブロック内で最初の区切り行を探す。ヘッダはその 1 つ前の行。
        int separatorIndex = -1;
        for (int i = start; i <= end; i++)
        {
            if (IsSeparatorLine(lines[i]))
            {
                separatorIndex = i;
                break;
            }
        }
        if (separatorIndex <= start)   // 区切り行が無い／先頭にあってヘッダが取れない
            return false;

        int headerLine = separatorIndex - 1;
        if (caretLine < headerLine)    // カーソルはテーブルより上の段落にある
            return false;

        var alignments = ParseAlignments(lines[separatorIndex]);

        var rows = new List<IReadOnlyList<string>> { ParseRow(lines[headerLine]) };
        for (int i = separatorIndex + 1; i <= end; i++)
            rows.Add(ParseRow(lines[i]));

        region = new MarkdownTableRegion(headerLine, end, rows, alignments);
        return true;
    }

    /// <summary>
    /// 行列と列揃えから Markdown テーブル本文（複数行・末尾改行なし）を組み立てる。列は内容幅に合わせて
    /// 桁揃えし、区切り行は列揃えを踏襲する。列数が揃わない行・揃え指定は空／None で補う。
    /// </summary>
    public static string SerializeTable(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<MarkdownColumnAlignment> alignments)
    {
        // 末尾の空行・空列（グリッド余白）を落とす。
        var trimmed = TrimEmpty(rows);
        if (trimmed.Count == 0)
            return string.Empty;

        int columnCount = trimmed.Max(r => r.Count);
        var aligns = Enumerable.Range(0, columnCount)
            .Select(c => c < alignments.Count ? alignments[c] : MarkdownColumnAlignment.None)
            .ToArray();

        // セルをエスケープ（'|' と改行）し、列ごとの表示幅を求める。区切りは最低 3 桁。
        var cells = trimmed
            .Select(r => Enumerable.Range(0, columnCount)
                .Select(c => c < r.Count ? EscapeCell(r[c]) : string.Empty)
                .ToArray())
            .ToArray();

        var widths = new int[columnCount];
        for (int c = 0; c < columnCount; c++)
            widths[c] = Math.Max(3, cells.Max(row => row[c].Length));

        var sb = new StringBuilder();
        // ヘッダ
        AppendRow(sb, cells[0], widths);
        // 区切り
        sb.Append("| ");
        for (int c = 0; c < columnCount; c++)
        {
            sb.Append(SeparatorMarker(aligns[c], widths[c]));
            sb.Append(c == columnCount - 1 ? " |" : " | ");
        }
        sb.Append('\n');
        // 本文
        for (int r = 1; r < cells.Length; r++)
            AppendRow(sb, cells[r], widths);

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 生成済みテーブル本文（<see cref="SerializeTable"/> の出力）を <paramref name="caretLine"/> の位置へ
    /// 挿入した行配列を返す。カーソル行が空行ならその行をテーブルで置き換え、非空行ならその直後へ挿入する。
    /// 前後の行が非空なら空行を 1 行挟み、テーブルが独立した Markdown ブロックになるようにする。
    /// </summary>
    public static string[] InsertTableAt(string[] lines, int caretLine, string table)
    {
        var tableLines = table.Split('\n');
        caretLine = Math.Clamp(caretLine, 0, Math.Max(0, lines.Length - 1));

        bool caretBlank = lines.Length > 0 && string.IsNullOrWhiteSpace(lines[caretLine]);
        int insertAt = lines.Length == 0 ? 0 : caretBlank ? caretLine : caretLine + 1;
        // 空行のカーソル行はテーブルへ置き換える（消費する）。
        int restStart = caretBlank ? insertAt + 1 : insertAt;

        var result = new List<string>(lines.Length + tableLines.Length + 2);
        result.AddRange(lines[..insertAt]);
        if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
            result.Add(string.Empty);
        result.AddRange(tableLines);
        if (restStart < lines.Length && !string.IsNullOrWhiteSpace(lines[restStart]))
            result.Add(string.Empty);
        result.AddRange(lines[restStart..]);
        return result.ToArray();
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        sb.Append("| ");
        for (int c = 0; c < cells.Length; c++)
        {
            sb.Append(cells[c].PadRight(widths[c]));
            sb.Append(c == cells.Length - 1 ? " |" : " | ");
        }
        sb.Append('\n');
    }

    private static string SeparatorMarker(MarkdownColumnAlignment align, int width) => align switch
    {
        MarkdownColumnAlignment.Left => ":" + new string('-', Math.Max(1, width - 1)),
        MarkdownColumnAlignment.Right => new string('-', Math.Max(1, width - 1)) + ":",
        MarkdownColumnAlignment.Center => ":" + new string('-', Math.Max(1, width - 2)) + ":",
        _ => new string('-', width),
    };

    /// <summary>末尾の空行・空列を落とす（グリッドが確保する余白セルを出力しない）。</summary>
    private static List<IReadOnlyList<string>> TrimEmpty(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        int lastRow = -1;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Any(v => !string.IsNullOrEmpty(v)))
            {
                lastRow = i;
                break;
            }
        }

        var result = new List<IReadOnlyList<string>>();
        for (int i = 0; i <= lastRow; i++)
        {
            var row = rows[i];
            int lastCol = -1;
            for (int j = row.Count - 1; j >= 0; j--)
            {
                if (!string.IsNullOrEmpty(row[j]))
                {
                    lastCol = j;
                    break;
                }
            }
            result.Add(row.Take(lastCol + 1).ToArray());
        }
        return result;
    }

    private static bool IsTableLine(string line)
        => !string.IsNullOrWhiteSpace(line) && line.Contains('|');

    private static bool IsSeparatorLine(string line)
    {
        var cells = ParseRow(line);
        return cells.Count > 0 && cells.All(c => SeparatorCell.IsMatch(c.Trim()));
    }

    private static List<MarkdownColumnAlignment> ParseAlignments(string separatorLine)
        => ParseRow(separatorLine).Select(cell =>
        {
            var c = cell.Trim();
            bool left = c.StartsWith(':');
            bool right = c.EndsWith(':');
            return (left, right) switch
            {
                (true, true) => MarkdownColumnAlignment.Center,
                (false, true) => MarkdownColumnAlignment.Right,
                (true, false) => MarkdownColumnAlignment.Left,
                _ => MarkdownColumnAlignment.None,
            };
        }).ToList();

    /// <summary>テーブル行 1 行をセル値へ分解する。前後の枠 '|' を落とし、エスケープ <c>\|</c> は復元する。</summary>
    private static List<string> ParseRow(string line)
    {
        var t = line.Trim();
        // 枠パイプ（行頭・行末の '|'）を取り除く。行末はエスケープ '\|' でないときのみ。
        bool leadingBorder = t.StartsWith('|');
        bool trailingBorder = t.EndsWith('|') && !t.EndsWith("\\|");
        if (leadingBorder)
            t = t[1..];
        if (trailingBorder && t.Length > 0)
            t = t[..^1];

        // エスケープされていない '|' で分割する。
        var parts = Regex.Split(t, @"(?<!\\)\|");
        return parts.Select(p => p.Replace("\\|", "|").Trim()).ToList();
    }

    private static string EscapeCell(string value)
        => (value ?? string.Empty)
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace("|", "\\|");
}
