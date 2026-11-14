using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// Claude Code の書き込み 1 回の中の 1 操作（§43.6 A3）。Edit は <see cref="Old"/> を <see cref="New"/> に置き換える
/// （<see cref="ReplaceAll"/> なら全部）。Write は <see cref="Whole"/>——ファイル全体が <see cref="New"/> になる。
/// </summary>
public sealed record ClaudeEditOp(string? Old, string New, bool ReplaceAll, bool Whole);

/// <summary>
/// 頼みごと単位の差分の純粋な部分（§43.6 A3）：記録の書き込みを読み、頼みごとの前の中身に順に当て直して
/// 「頼みごとの後」を作る。I/O を持たない（前の中身をどこから取るかは Services の <c>ClaudeRequestDiffBuilder</c>）。
/// 改行は LF にそろえて扱う——記録の中身は LF、ディスクのファイルは CRLF のことがある。
/// </summary>
public static class ClaudeRequestDiff
{
    /// <summary>記録の 1 行（assistant の行）から、<paramref name="toolUseId"/> の書き込みの操作を読む。
    /// 当て直せないツール（NotebookEdit）・書き込みでないツールは null。</summary>
    public static IReadOnlyList<ClaudeEditOp>? ReadOperations(string transcriptLine, string toolUseId)
    {
        try
        {
            using var doc = JsonDocument.Parse(transcriptLine);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || Str(block, "type") != "tool_use" || Str(block, "id") != toolUseId
                    || !block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                    continue;
                return Str(block, "name") switch
                {
                    "Write" => [new ClaudeEditOp(null, Normalize(Str(input, "content")), false, true)],
                    "Edit" => [EditOf(input)],
                    "MultiEdit" when input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array
                        => edits.EnumerateArray().Where(edit => edit.ValueKind == JsonValueKind.Object).Select(EditOf).ToList(),
                    _ => null,
                };
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 頼みごとの前の中身に、操作を順に当て直す。当たらない操作（置換前が見つからない——ツールを通さない変更が
    /// 間に入った）が 1 つでもあれば null：半端な「後」は作らない。
    /// </summary>
    public static string? Replay(string before, IEnumerable<ClaudeEditOp> operations)
    {
        var text = Normalize(before);
        foreach (var op in operations)
        {
            if (op.Whole)
            {
                text = op.New;
                continue;
            }
            if (string.IsNullOrEmpty(op.Old))
            {
                // 置換前が空の Edit は新しいファイルを作る。中身があるファイルには当てようがない。
                if (text.Length > 0)
                    return null;
                text = op.New;
                continue;
            }
            var at = text.IndexOf(op.Old, StringComparison.Ordinal);
            if (at < 0)
                return null;
            text = op.ReplaceAll
                ? text.Replace(op.Old, op.New, StringComparison.Ordinal)
                : string.Concat(text.AsSpan(0, at), op.New, text.AsSpan(at + op.Old.Length));
        }
        return text;
    }

    /// <summary>
    /// 前の中身が分からないときの代わり：操作ごとの置換前と置換後だけを並べる（抜粋）。行番号はファイルのものではない。
    /// Write は前が分からないので、左は空（全行が足した行に見える）。
    /// </summary>
    public static (string Left, string Right) Excerpt(IEnumerable<ClaudeEditOp> operations)
    {
        var left = new StringBuilder();
        var right = new StringBuilder();
        foreach (var op in operations)
        {
            if (op.Whole)
            {
                left.Clear();
                right.Clear();
            }
            else if (left.Length > 0 || right.Length > 0)
            {
                // 操作の切れ目。両側に同じ行を置いて、差分の上では変わっていない行として並ぶようにする。
                left.Append("⋯\n");
                right.Append("⋯\n");
            }
            Append(left, op.Old);
            Append(right, op.New);
        }
        return (left.ToString(), right.ToString());

        static void Append(StringBuilder to, string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            to.Append(text);
            if (text[^1] != '\n')
                to.Append('\n');
        }
    }

    /// <summary>改行を LF にそろえる。</summary>
    public static string Normalize(string? text)
        => string.IsNullOrEmpty(text) ? "" : text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static ClaudeEditOp EditOf(JsonElement input)
        => new(Normalize(Str(input, "old_string")), Normalize(Str(input, "new_string")),
            input.TryGetProperty("replace_all", out var all) && all.ValueKind == JsonValueKind.True, false);

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
