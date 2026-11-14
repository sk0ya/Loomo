using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// Claude Code がファイルへ書いた 1 回ぶん（§43 出自）。<see cref="Lines"/> は<b>その書き込みが足した行</b>
/// （行末の空白を落としたもの）——Edit の置換前にもあった行は、この書き込みが足したものではないので入れない。
/// </summary>
public sealed record ClaudeWrite(
    string SessionId,
    string TranscriptPath,
    string FilePath,
    string Prompt,
    int PromptLine,
    DateTimeOffset? At,
    IReadOnlyList<string> Lines,
    bool FromSubagent)
{
    /// <summary>頼みごとの 1 行目（行末の注記・一覧の題に使う）。</summary>
    public string PromptHead => ClaudeProvenance.Head(Prompt);

    /// <summary>この書き込みの操作（tool_use）が記録のどこにあるか——頼みごと単位の差分（§43.6 A3）が中身を読み直すための場所。
    /// サブエージェントの操作は <see cref="TranscriptPath"/>（本体の記録）ではなくサブエージェントの記録にある。</summary>
    public string? SourcePath { get; init; }

    /// <summary><see cref="SourcePath"/> の中の行番号（1 始まり）。</summary>
    public int SourceLine { get; init; }

    /// <summary>操作の結果（tool_result）の行番号（1 始まり・無ければ 0）。</summary>
    public int ResultLine { get; init; }

    public string? ToolUseId { get; init; }

    /// <summary>置き場から読み戻したもの（§43.6 A2b）。頼みごとの本文・セッション・位置だけで、足した行は持たない——
    /// 元の記録はもう無いかもしれない。</summary>
    public bool Stored { get; init; }
}

/// <summary>
/// 出自（§43）：コミットしていない行を、それを書いた Claude Code の頼みごとへ結ぶ。記録に残るのは行番号ではなく
/// <b>書いた中身</b>（Edit の <c>new_string</c>・Write の <c>content</c>）なので、行は中身で合わせる——その後に人間や
/// 別の編集が行をずらしていても当たる。I/O を持たない（記録の読み方は Services の索引が持つ）。
/// </summary>
public static class ClaudeProvenance
{
    /// <summary>1 回の書き込みから持つ行の上限。巨大な生成物（ロックファイル等）を丸ごと抱えない。</summary>
    public const int MaxLinesPerWrite = 5000;

    private const int HeadMax = 80;

    /// <summary>
    /// 記録の 1 行（assistant の行）から、<paramref name="toolUseId"/> の書き込みが足した行を読む。
    /// 書き込むツール（Edit／MultiEdit／Write／NotebookEdit）でなければ null。
    /// </summary>
    public static IReadOnlyList<string>? ReadAddedLines(string transcriptLine, string toolUseId)
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
                    "Write" => Added(null, Str(input, "content")),
                    "Edit" => Added(Str(input, "old_string"), Str(input, "new_string")),
                    "NotebookEdit" => Added(null, Str(input, "new_source")),
                    "MultiEdit" when input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array
                        => edits.EnumerateArray()
                            .Where(edit => edit.ValueKind == JsonValueKind.Object)
                            .SelectMany(edit => Added(Str(edit, "old_string"), Str(edit, "new_string")))
                            .Take(MaxLinesPerWrite).ToList(),
                    _ => null,
                };
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>置換後にあって置換前に無い行。前後の文脈として <c>old_string</c> にも入っている行は足した行ではない。</summary>
    internal static IReadOnlyList<string> Added(string? oldText, string? newText)
    {
        if (string.IsNullOrEmpty(newText))
            return [];
        // 同じ行が置換前に n 回あれば、置換後の最初の n 回は「元からあった」とみなす。
        var before = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in SplitLines(oldText))
            before[line] = before.GetValueOrDefault(line) + 1;
        var added = new List<string>();
        foreach (var line in SplitLines(newText))
        {
            if (before.TryGetValue(line, out var remaining) && remaining > 0)
            {
                before[line] = remaining - 1;
                continue;
            }
            added.Add(line);
            if (added.Count >= MaxLinesPerWrite)
                break;
        }
        return added;
    }

    /// <summary>
    /// コミットしていない行（0 始まりの行番号 → 中身）を書き込みへ結ぶ。<paramref name="writes"/> は古い順。
    /// <list type="bullet">
    /// <item>中身のある行で、足した書き込みが 1 つだけなら、それで決まり。</item>
    /// <item>複数の書き込みにある行（<c>return result;</c>）は、続いている行の持ち主がその行も足していればそこへ。
    ///   手がかりが無ければ<b>いちばん新しい</b>書き込みへ。</item>
    /// <item><c>}</c> や空行のようにどこにでもある行は中身では決めず、続いている行の持ち主に寄せるだけ（寄せ先が無ければ結ばない）。</item>
    /// <item>どの書き込みにも無い行は結ばない——人間が書いた行。</item>
    /// </list>
    /// </summary>
    public static Dictionary<int, ClaudeWrite> Attribute(
        IReadOnlyDictionary<int, string> uncommitted, IReadOnlyList<ClaudeWrite> writes)
    {
        var result = new Dictionary<int, ClaudeWrite>();
        if (uncommitted.Count == 0 || writes.Count == 0)
            return result;

        var sets = writes.Select(write => new HashSet<string>(write.Lines, StringComparer.Ordinal)).ToList();

        // 行ごとに、その中身を足した書き込み（古い順の番号）。1 つしか無い行は、それで決まり。
        var candidates = new Dictionary<int, List<int>>();
        var undecided = new List<int>();
        foreach (var (line, raw) in uncommitted)
        {
            var text = raw.TrimEnd();
            var owners = new List<int>();
            for (var i = 0; i < writes.Count; i++)
            {
                if (sets[i].Contains(text))
                    owners.Add(i);
            }
            candidates[line] = owners;
            if (owners.Count == 0)
                continue;
            if (owners.Count == 1 && IsDistinctive(text))
                result[line] = writes[owners[0]];
            else
                undecided.Add(line);
        }

        // 複数の書き込みにある行（`return result;`）・どこにでもある行（`}`・空行）は、続いている行の持ち主に寄せる。
        // 上から順に決めるので、決まった行が次の行の手がかりになる。
        var number = new Dictionary<ClaudeWrite, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < writes.Count; i++)
            number[writes[i]] = i;
        undecided.Sort();
        foreach (var line in undecided)
        {
            var owners = candidates[line];
            if (Neighbor(line, -1) is { } above)
                result[line] = above;
            else if (Neighbor(line, +1) is { } below)
                result[line] = below;
            else if (IsDistinctive(uncommitted[line].TrimEnd()))
                result[line] = writes[owners[^1]]; // 手がかりが無ければ、いちばん新しい書き込み

            // 隣へ辿って、この行も足している書き込みに結ばれた最初の行。人間の行（どの書き込みにも無い、中身のある行）で止まる。
            ClaudeWrite? Neighbor(int from, int step)
            {
                const int reach = 40;
                for (var at = from + step; Math.Abs(at - from) <= reach && uncommitted.TryGetValue(at, out var other); at += step)
                {
                    if (result.TryGetValue(at, out var write))
                    {
                        if (owners.Contains(number[write]))
                            return write;
                    }
                    else if (candidates[at].Count == 0 && IsDistinctive(other.TrimEnd()))
                        return null;
                }
                return null;
            }
        }
        return result;
    }

    /// <summary>中身で持ち主を決めてよい行か。括弧だけ・空行・<c>else</c> のような短い行はどの書き込みにもある。</summary>
    public static bool IsDistinctive(string line)
    {
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length < 6)
            return false;
        var letters = 0;
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c) && ++letters >= 4)
                return true;
        }
        return false;
    }

    /// <summary>頼みごとの 1 行目を <see cref="HeadMax"/> 字まで。</summary>
    public static string Head(string prompt)
    {
        var text = prompt.AsSpan().Trim();
        var end = text.IndexOfAny('\r', '\n');
        if (end >= 0)
            text = text[..end].TrimEnd();
        return text.Length <= HeadMax ? text.ToString() : string.Concat(text[..(HeadMax - 1)], "…");
    }

    private static IEnumerable<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        foreach (var line in text.Split('\n'))
            yield return line.TrimEnd();
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
