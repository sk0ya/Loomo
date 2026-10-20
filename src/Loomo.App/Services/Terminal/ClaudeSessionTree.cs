using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Services;

/// <summary>ツリーの節の種類（§39.9）。</summary>
internal enum ClaudeTreeKind
{
    /// <summary>会話をすべて（§39.4 の1本の読み物）。</summary>
    Conversation,
    Turn,
    /// <summary>返事の節（返事のいちばん上の段の見出しで切ったもの。見出しが無ければ返事全体）。</summary>
    Section,
    /// <summary>作業の途中のメモ（最後より前の本文）。</summary>
    Note,
    /// <summary>書き換えたファイル（そのターン・そのエージェントでの書き換えをまとめる）。</summary>
    File,
    Command,
    /// <summary>サブエージェント。子はそのエージェントの操作。</summary>
    Agent,
    /// <summary>失敗した操作（コマンド以外も）。</summary>
    Failure,
}

/// <summary>ツリーに出す種類（トグル）と絞り込みの文字。</summary>
internal sealed record ClaudeTreeFilter(
    bool Replies = true, bool Changes = true, bool Agents = true,
    bool Commands = false, bool Failures = false, bool Notes = false, string Text = "")
{
    public bool AnyCategory => Replies || Changes || Agents || Commands || Failures || Notes;
}

/// <summary>
/// ツリーの節1つ。<see cref="Id"/> は記録の位置から作るので、記録が書き足されても・トグルを替えても同じ節は同じ id
/// （開閉・選択を持ち越す鍵）。<see cref="Key"/> は描き直しが要るかの指紋。
/// </summary>
internal sealed record ClaudeTreeNode(string Id, ClaudeTreeKind Kind, string Label, IReadOnlyList<ClaudeTreeNode> Children)
{
    /// <summary>行の右に添える薄い一言（時刻・件数）。</summary>
    public string Hint { get; init; } = "";
    public bool Failed { get; init; }
    /// <summary>作業中のターン（またはその返事）。</summary>
    public bool Working { get; init; }
    public string Key { get; init; } = "";
    public ClaudeTurn? Turn { get; init; }
    /// <summary>節・メモの Markdown。</summary>
    public string? Text { get; init; }
    public string? FilePath { get; init; }
    /// <summary>コマンド・失敗は1つ、ファイルはそのファイルへの書き換えすべて。</summary>
    public IReadOnlyList<RunToolCall> Calls { get; init; } = [];
    public ClaudeSubagent? Agent { get; init; }
    /// <summary>絞り込みの文字を当てる中身（題に加えて、返事の本文・コマンド全文・エージェントへの頼みごと）。</summary>
    internal string SearchText { get; init; } = "";
}

/// <summary>
/// Claude Code の会話をツリーに組む（§39.9）。<b>ターン軸</b>：ターン（新しい順）の下に、返事の節・書き換えたファイル・
/// サブエージェント（その下にそのエージェントの操作）・コマンド・失敗・途中のメモを、トグルで選んだ種類だけ並べる。
/// 葉はどれも「押すと、ターミナルでは見られない形に描き直した中身が出る」もの——要約1行で終わるものは置かない（§39.1）。
/// 純粋な関数（I/O なし）。中身の描き方は <see cref="ClaudeConversationDocument.BuildNodeBody"/>。
/// </summary>
internal static class ClaudeSessionTree
{
    public const string ConversationId = "all";

    private const int LabelChars = 60;

    public static IReadOnlyList<ClaudeTreeNode> Build(
        ClaudeSessionDigest digest, ClaudeTreeFilter filter, Func<string, bool> inWorkspace, Func<string, string> displayPath, bool working)
    {
        var roots = new List<ClaudeTreeNode>();
        var searching = filter.Text.Trim().Length > 0;
        if (!searching && digest.Turns.Count > 0)
            roots.Add(new ClaudeTreeNode(ConversationId, ClaudeTreeKind.Conversation, "会話をすべて", [])
            {
                Hint = $"{digest.Turns.Count} ターン",
                Key = string.Join(',', digest.Turns.Select((t, i) => ClaudeConversationDocument.TurnKey(t, working && i == digest.Turns.Count - 1))),
            });

        for (var i = digest.Turns.Count - 1; i >= 0; i--)
        {
            var turn = digest.Turns[i];
            var latest = i == digest.Turns.Count - 1;
            var inProgress = latest && working;
            var node = TurnNode(turn, inProgress, filter, inWorkspace, displayPath);
            if (searching)
            {
                if (Matches(node, filter.Text))
                    roots.Add(node);   // 頼みごとに当たった：ターンの中身はそのまま
                else if (Prune(node, filter.Text) is { Children.Count: > 0 } pruned)
                    roots.Add(pruned);
                continue;
            }
            // 選んだ種類の葉が1つも無いターンは出さない（最新のターンは、作業の始まりでも出す）。
            if (latest || node.Children.Count > 0 || !filter.AnyCategory)
                roots.Add(node);
        }
        return roots;
    }

    private static ClaudeTreeNode TurnNode(
        ClaudeTurn turn, bool inProgress, ClaudeTreeFilter filter, Func<string, bool> inWorkspace, Func<string, string> displayPath)
    {
        var id = "t" + turn.PromptLine;
        var children = new List<ClaudeTreeNode>();
        if (filter.Replies && turn.Reply is { } reply)
        {
            var sections = SplitSections(reply);
            for (var s = 0; s < sections.Count; s++)
                children.Add(new ClaudeTreeNode($"{id}/s{s}", ClaudeTreeKind.Section, sections[s].Title, [])
                {
                    Turn = turn, Text = sections[s].Markdown, Working = inProgress, Key = sections[s].Markdown.Length.ToString(),
                    SearchText = sections[s].Markdown,
                });
        }
        // サブエージェントを出すときは、その操作はエージェントの下へ（ターンの直下は本体の操作だけ）。
        var calls = filter.Agents ? turn.Calls.Where(c => !c.FromSubagent).ToList() : turn.Calls.ToList();
        children.AddRange(Operations(id, turn, calls, filter, inWorkspace, displayPath));
        if (filter.Agents)
        {
            foreach (var agent in turn.Agents)
                children.Add(AgentNode(id, turn, agent, filter, inWorkspace, displayPath));
        }
        if (filter.Notes && turn.Replies.Count > 1)
        {
            for (var n = 0; n < turn.Replies.Count - 1; n++)
                children.Add(new ClaudeTreeNode($"{id}/n{n}", ClaudeTreeKind.Note, "メモ: " + Shorten(FirstLine(turn.Replies[n])), [])
                {
                    Turn = turn, Text = turn.Replies[n], Key = turn.Replies[n].Length.ToString(), SearchText = turn.Replies[n],
                });
        }

        var failures = turn.Failures.Count();
        return new ClaudeTreeNode(id, ClaudeTreeKind.Turn, Shorten(FirstLine(turn.Prompt)), children)
        {
            Hint = string.Join(" ", new[]
            {
                turn.At is { } at ? LocalTime(at) : null,
                failures > 0 ? $"✗{failures}" : null,
            }.Where(s => s is not null)),
            Failed = failures > 0,
            Working = inProgress,
            Turn = turn,
            Key = ClaudeConversationDocument.TurnKey(turn, inProgress),
            SearchText = turn.Prompt,
        };
    }

    /// <summary>書き換えたファイル・コマンド・失敗（ターンの直下とエージェントの下で共通）。</summary>
    private static IEnumerable<ClaudeTreeNode> Operations(
        string parentId, ClaudeTurn turn, IReadOnlyList<RunToolCall> calls, ClaudeTreeFilter filter,
        Func<string, bool> inWorkspace, Func<string, string> displayPath)
    {
        if (filter.Changes)
        {
            // ワークスペースの外（Claude の一時ファイル・作業メモ）はツリーに出さない——会話の読み物の脇注と同じ扱い。
            foreach (var group in calls
                         .Where(c => c.FilePath is { Length: > 0 } && IsWrite(c.Tool) && !c.IsRejected && !c.IsError && inWorkspace(c.FilePath))
                         .GroupBy(c => c.FilePath!, StringComparer.OrdinalIgnoreCase))
            {
                var edits = group.ToList();
                var path = edits[^1].FilePath!;
                yield return new ClaudeTreeNode($"{parentId}/f:{path.ToLowerInvariant()}", ClaudeTreeKind.File, Path.GetFileName(path), [])
                {
                    Hint = edits.Count > 1 ? $"×{edits.Count}" : edits[0].Tool == "Write" ? "作成" : "",
                    Turn = turn, FilePath = path, Calls = edits, Key = edits.Count.ToString(), SearchText = displayPath(path),
                };
            }
        }
        if (filter.Commands)
        {
            foreach (var call in calls.Where(c => c.Command is not null && !c.IsRejected))
                yield return new ClaudeTreeNode($"{parentId}/c:{CallId(call)}", ClaudeTreeKind.Command, Shorten(FirstLine(call.Command!)), [])
                {
                    Hint = call.IsError ? "✗" : call.HasResult ? "" : "…",
                    Failed = call.IsError, Turn = turn, Calls = [call], Key = CallKey(call), SearchText = call.Command!,
                };
        }
        if (filter.Failures)
        {
            // コマンドを出しているなら、失敗したコマンドはそちらに ✗ 付きで出ている。
            foreach (var call in calls.Where(c => c.IsError && !(filter.Commands && c.Command is not null)))
                yield return new ClaudeTreeNode($"{parentId}/x:{CallId(call)}", ClaudeTreeKind.Failure, $"{call.Tool}: {Shorten(call.Summary)}", [])
                {
                    Failed = true, Turn = turn, Calls = [call], Key = CallKey(call), SearchText = call.Summary + " " + call.ErrorHead,
                };
        }
    }

    private static ClaudeTreeNode AgentNode(
        string turnId, ClaudeTurn turn, ClaudeSubagent agent, ClaudeTreeFilter filter,
        Func<string, bool> inWorkspace, Func<string, string> displayPath)
    {
        var id = $"{turnId}/a:{agent.AgentId}";
        var children = Operations(id, turn, agent.Calls, filter, inWorkspace, displayPath).ToList();
        var failures = agent.Calls.Count(c => c.IsError);
        var running = agent.Launch is { HasResult: false } || agent.Report is null;
        return new ClaudeTreeNode(id, ClaudeTreeKind.Agent, Shorten(agent.Name ?? FirstLine(agent.Prompt ?? agent.AgentId)), children)
        {
            Hint = string.Join(" ", new[]
            {
                $"{agent.Calls.Count} 操作",
                failures > 0 ? $"✗{failures}" : null,
            }.Where(s => s is not null)),
            Failed = failures > 0,
            Working = running,
            Turn = turn,
            Agent = agent,
            Calls = agent.Launch is { } launch ? [launch] : [],
            Key = string.Join('|', agent.Calls.Count, agent.Replies.Count, agent.Report?.Length, agent.Launch?.HasResult),
            SearchText = (agent.Name ?? "") + " " + agent.Prompt + " " + agent.Report,
        };
    }

    /// <summary>節の題か中身に当たるか（大文字小文字を区別しない）。</summary>
    private static bool Matches(ClaudeTreeNode node, string text)
    {
        var needle = text.Trim();
        return node.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || node.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当たった節と、その親だけを残す。</summary>
    private static ClaudeTreeNode? Prune(ClaudeTreeNode node, string text)
    {
        var children = new List<ClaudeTreeNode>();
        foreach (var child in node.Children)
        {
            if (Matches(child, text))
                children.Add(child);
            else if (Prune(child, text) is { Children.Count: > 0 } pruned)
                children.Add(pruned);
        }
        return children.Count > 0 ? node with { Children = children } : null;
    }

    /// <summary>
    /// 返事を節に切る：コードブロックの外の、いちばん上の段の見出しで。見出しより前に本文があれば、それも1節（題はその1行目）。
    /// 見出しが無ければ返事全体で1節。
    /// </summary>
    internal static IReadOnlyList<(string Title, string Markdown)> SplitSections(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var headings = new List<(int Line, int Level, string Title)>();
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }
            if (!fence && Heading.Match(lines[i]) is { Success: true } m)
                headings.Add((i, m.Groups[1].Length, m.Groups[2].Value.Trim().TrimEnd('#').Trim()));
        }
        if (headings.Count == 0)
            return [(Shorten(PlainFirstLine(markdown)), markdown.Trim())];

        var top = headings.Min(h => h.Level);
        var cuts = headings.Where(h => h.Level == top).ToList();
        var sections = new List<(string, string)>();
        var lead = string.Join("\n", lines[..cuts[0].Line]).Trim();
        if (lead.Length > 0)
            sections.Add((Shorten(PlainFirstLine(lead)), lead));
        for (var c = 0; c < cuts.Count; c++)
        {
            var end = c + 1 < cuts.Count ? cuts[c + 1].Line : lines.Length;
            sections.Add((Shorten(cuts[c].Title), string.Join("\n", lines[cuts[c].Line..end]).Trim()));
        }
        return sections;
    }

    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);

    private static bool IsWrite(string tool) => tool is "Edit" or "MultiEdit" or "Write" or "NotebookEdit";

    /// <summary>操作の id。tool_use の id が一番確か（ファイルが書き足されても変わらない）。</summary>
    private static string CallId(RunToolCall call) => call.ToolUseId ?? $"{Path.GetFileName(call.TranscriptPath)}:{call.LineNumber}";

    private static string CallKey(RunToolCall call) => string.Join('|', call.HasResult, call.IsError, call.ResultLineNumber);

    private static string FirstLine(string text)
        => text.Trim().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>Markdown の1行目を、見出しの # や強調の記号を外して。</summary>
    private static string PlainFirstLine(string markdown)
    {
        var line = FirstLine(markdown).TrimStart('#', '>', '-', '*', ' ');
        return line.Replace("**", "").Replace("`", "");
    }

    private static string Shorten(string text)
    {
        var line = text.Replace('\t', ' ');
        return line.Length <= LabelChars ? line : line[..(LabelChars - 1)] + "…";
    }

    private static string LocalTime(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("M/d HH:mm");
    }
}
