using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>Claude Code が書き換えたファイル1つ（同じファイルへの編集はまとめる）。</summary>
public sealed record ClaudeTouchedFile(string Path, int Edits, DateTimeOffset? LastAt, bool Created);

/// <summary>
/// ターン＝人間の頼みごと1つと、それに応えた Claude の操作と返事（§39.4）。横で見ている人が知りたいのは
/// 「自分の頼みに対して何が起きたか」なので、操作は種類ではなくターンで束ねる。
/// </summary>
public sealed record ClaudeTurn(
    string Prompt,
    int PromptLine,
    DateTimeOffset? At,
    IReadOnlyList<RunToolCall> Calls,
    IReadOnlyList<ClaudeTouchedFile> Files,
    IReadOnlyList<string> Replies,
    DateTimeOffset? LastAt,
    IReadOnlyList<ClaudeSubagent>? AgentList = null)
{
    /// <summary>このターンで動いたサブエージェント（記録のファイルごと、現れた順）。<see cref="Calls"/> にも
    /// サブエージェントの操作は入っている——こちらは「どのエージェントの操作か」で束ねたもの（§39.9）。</summary>
    public IReadOnlyList<ClaudeSubagent> Agents => AgentList ?? [];

    /// <summary>このターンの最後の本文。ターンが終わっていれば Claude の返事、作業中なら途中のメモ。</summary>
    public string? Reply => Replies.Count > 0 ? Replies[^1] : null;

    /// <summary>シェルのコマンドを走らせた回数。</summary>
    public int Commands => Calls.Count(c => c.Command is not null);

    /// <summary>失敗した操作（人間の拒否は含まない）。</summary>
    public IEnumerable<RunToolCall> Failures => Calls.Where(c => c.IsError);

    /// <summary>このターンで呼んだサブエージェントの数（本体の Agent／Task 呼び出し）。</summary>
    public int Subagents => Calls.Count(c => !c.FromSubagent && c.Tool is "Agent" or "Task");
}

/// <summary>
/// サブエージェント1つ（<c>&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>、§39.9）。<see cref="Launch"/> は本体でそれを
/// 起動した Agent／Task の呼び出し——結果に載る <c>agentId</c> か、渡した頼みごとと記録の最初の行が同じことで結ぶ。
/// フォークしたスキル（<c>/code-review</c> など）は Agent 呼び出しを経ないので null。
/// </summary>
public sealed record ClaudeSubagent(
    string TranscriptPath,
    string AgentId,
    string? Name,
    string? Prompt,
    DateTimeOffset? StartedAt,
    IReadOnlyList<RunToolCall> Calls,
    IReadOnlyList<ClaudeTouchedFile> Files,
    IReadOnlyList<string> Replies,
    RunToolCall? Launch)
{
    /// <summary>最後の本文＝エージェントの報告（作業中なら途中のメモ）。</summary>
    public string? Report => Replies.Count > 0 ? Replies[^1] : null;
}

/// <summary>サブエージェントの記録から読んだもの（呼び出し以外）。<see cref="ClaudeSessionFollower"/> が追記分ずつ足す。</summary>
public sealed class ClaudeSubagentText
{
    public required string TranscriptPath { get; init; }
    public required string AgentId { get; init; }
    /// <summary><c>agent-&lt;id&gt;.meta.json</c> の description（無ければ agentType）。</summary>
    public string? Name { get; set; }
    /// <summary>記録の最初の user の本文＝エージェントへの頼みごと。</summary>
    public string? Prompt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public List<string> Replies { get; } = new();
}

/// <summary>assistant の本文1つ（本体の記録の1始まりの行番号つき）。</summary>
public readonly record struct ClaudeReplyText(int Line, DateTimeOffset? At, string Text);

/// <summary>
/// 動いている Claude Code のセッション1つを、ターミナルサポートの横の面（§39.4）に出す形へ畳んだもの。
/// 記録の読み方はハーネスペインの <see cref="ClaudeTranscriptParser"/>（ツール呼び出し・頼みごと・料金）と
/// <see cref="ClaudeSessionCatalog"/>（題名・コンテキスト量）をそのまま使う——読み方を二つ持つと、片方だけ
/// 記録の形の変化に追従して食い違う。ここで足すのは返事の本文（assistant の text）を読むことだけ。
/// </summary>
public sealed record ClaudeSessionDigest(
    string SessionId,
    string TranscriptPath,
    DateTime LastWriteUtc,
    string? Title,
    string? Model,
    long? ContextTokens,
    decimal? CostUsd,
    DateTimeOffset? StartedAt,
    IReadOnlyList<ClaudeTurn> Turns,
    IReadOnlyList<ClaudeTouchedFile> SessionFiles,
    RunToolCall? PendingCall,
    int ToolCallCount,
    int ToolErrorCount)
{
    /// <summary>持っておくターンの上限（古いものから捨てる）。古いものは記録を開けば辿れる。</summary>
    public const int MaxTurns = 20;

    /// <summary>返事の本文を持つ上限（字）。返事は読み物として全文を描き、ツリーは節ごとに引く（§39.9）ので、
    /// 2000 字で切っていた頃は後ろの節がツリーから消えていた。持つのは新しい <see cref="MaxTurns"/> ターンぶんだけ。</summary>
    public const int MaxReplyChars = 20_000;

    /// <summary>いまのターン（最後の頼みごと）。頼みごとがまだ無ければ null。</summary>
    public ClaudeTurn? CurrentTurn => Turns.Count > 0 ? Turns[^1] : null;

    /// <summary>ファイルを書き換えるツール。<c>Read</c> は触ったうちに入れない（読んだだけのファイルを並べると、
    /// 「何が変わったか」を見に来た人の目が埋もれる）。</summary>
    private static readonly HashSet<string> WritingTools = new(StringComparer.Ordinal)
    {
        "Edit", "MultiEdit", "Write", "NotebookEdit",
    };

    /// <summary>記録ファイルを読んで畳む（サブエージェントの記録も）。書き込み中でも読める。
    /// 続けて読み直すなら <see cref="ClaudeSessionFollower"/>（追記分だけ読む）。</summary>
    public static ClaudeSessionDigest? Read(string transcriptPath)
        => new ClaudeSessionFollower(transcriptPath).Refresh().Digest;

    /// <summary>行の列から畳む（I/O なし。テストは文字列で書ける）。</summary>
    public static ClaudeSessionDigest Build(string sessionId, string transcriptPath, DateTime lastWriteUtc, IReadOnlyList<string> lines)
    {
        var follower = new ClaudeSessionFollower(transcriptPath, sessionId);
        follower.Feed(transcriptPath, lines, isSubagent: false);
        return follower.Compose(lastWriteUtc);
    }

    /// <summary>読み終わりの材料（Run・要約・本文）から畳む。</summary>
    internal static ClaudeSessionDigest Compose(
        string sessionId, string transcriptPath, DateTime lastWriteUtc,
        HarnessRun run, ClaudeSessionSummary summary, IReadOnlyList<ClaudeReplyText> replies,
        IReadOnlyList<ClaudeSubagentText>? subagentTexts = null)
    {
        var agents = Subagents(run, subagentTexts ?? []);
        var turns = new List<ClaudeTurn>();
        var marks = run.PromptMarks;
        var first = Math.Max(0, marks.Count - MaxTurns);
        for (var i = first; i < marks.Count; i++)
        {
            var start = marks[i];
            var end = i + 1 < marks.Count ? marks[i + 1] : (RunPromptMark?)null;
            // 本体の操作は行番号で区切る。サブエージェントの操作は別ファイルなので時刻で区切る。
            var calls = run.ToolCalls.Where(call => call.FromSubagent
                    ? call.At is { } at && (start.At is null || at >= start.At) && (end?.At is not { } until || at < until)
                    : call.LineNumber > start.LineNumber && (end is null || call.LineNumber < end.Value.LineNumber))
                .ToList();
            var texts = replies.Where(r => r.Line > start.LineNumber && (end is null || r.Line < end.Value.LineNumber)).ToList();
            var lastAt = new[] { calls.LastOrDefault()?.At, texts.Count > 0 ? texts[^1].At : null }.Max();
            // サブエージェントは起動した呼び出しのターンへ。結べなければ（フォークしたスキル）動き始めた時刻で。
            var turnAgents = agents.Where(agent => agent.Launch is { } launch
                    ? launch.LineNumber > start.LineNumber && (end is null || launch.LineNumber < end.Value.LineNumber)
                    : agent.StartedAt is { } began && (start.At is null || began >= start.At) && (end?.At is not { } stop || began < stop))
                .ToList();
            turns.Add(new ClaudeTurn(run.Prompts[i], start.LineNumber, start.At, calls, TouchedFiles(calls),
                texts.Select(r => r.Text).ToList(), lastAt, turnAgents));
        }

        // 結果がまだ無い最後の操作＝いま実行中か、人間の承認を待っているもの。最後の1つに限る——途中の結果欠けは
        // 中断（Esc）の名残りで、いま起きていることではない。
        var last = run.ToolCalls.LastOrDefault(c => !c.FromSubagent);
        var pending = last is { HasResult: false } ? last : null;

        return new ClaudeSessionDigest(
            sessionId, transcriptPath, lastWriteUtc,
            summary.Title, summary.Model, summary.ContextTokens, run.CostUsd, run.StartedAt,
            turns, TouchedFiles(run.ToolCalls), pending,
            run.ToolCallCountExcludingRejected, run.ToolErrorCount);
    }

    /// <summary>サブエージェントの記録ごとに、その操作と起動した呼び出しを束ねる。</summary>
    private static List<ClaudeSubagent> Subagents(HarnessRun run, IReadOnlyList<ClaudeSubagentText> texts)
    {
        var launches = run.ToolCalls.Where(c => !c.FromSubagent && c.Tool is "Agent" or "Task").ToList();
        var used = new HashSet<RunToolCall>();
        var agents = new List<ClaudeSubagent>();
        foreach (var text in texts)
        {
            var calls = run.ToolCalls.Where(c => c.FromSubagent
                && string.Equals(c.TranscriptPath, text.TranscriptPath, StringComparison.OrdinalIgnoreCase)).ToList();
            // 結果の agentId が一番確か。まだ結果の無い（動いている）エージェントは、渡した頼みごとが記録の最初の行と同じもの。
            var launch = launches.FirstOrDefault(c => !used.Contains(c) && c.AgentId == text.AgentId)
                ?? launches.FirstOrDefault(c => !used.Contains(c) && c.AgentId is null && SamePrompt(c.AgentPrompt, text.Prompt));
            if (launch is not null)
                used.Add(launch);
            agents.Add(new ClaudeSubagent(text.TranscriptPath, text.AgentId,
                text.Name ?? launch?.Summary, text.Prompt, text.StartedAt ?? calls.FirstOrDefault()?.At,
                calls, TouchedFiles(calls), text.Replies.ToList(), launch));
        }
        return agents;
    }

    private static bool SamePrompt(string? launched, string? recorded)
        => launched is { Length: > 0 } && recorded is { Length: > 0 }
           && (recorded.Length > launched.Length ? recorded.StartsWith(launched, StringComparison.Ordinal)
                                                 : string.Equals(launched.Trim(), recorded.Trim(), StringComparison.Ordinal));

    private static IReadOnlyList<ClaudeTouchedFile> TouchedFiles(IEnumerable<RunToolCall> calls)
        => calls
            .Where(call => call.FilePath is { Length: > 0 } && WritingTools.Contains(call.Tool) && !call.IsRejected && !call.IsError)
            .GroupBy(call => call.FilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClaudeTouchedFile(
                group.Last().FilePath!,
                group.Count(),
                group.Max(call => call.At),
                // 最初の書き込みが Write なら「作った」。後から Edit されても作ったことに変わりはない。
                group.First().Tool == "Write"))
            .OrderByDescending(file => file.LastAt ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>サブエージェントの記録の最初の user の行（＝エージェントへの頼みごと）と時刻を読む。</summary>
    internal static void ReadSubagentStart(string line, ClaudeSubagentText agent)
    {
        if (!line.Contains("\"user\"", StringComparison.Ordinal))
            return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "user"
                || !root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                return;
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()
                : content.ValueKind == JsonValueKind.Array
                    ? string.Concat(content.EnumerateArray()
                        .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var t) && t.GetString() == "text"
                                    && b.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String)
                        .Select(b => b.GetProperty("text").GetString()))
                    : null;
            if (string.IsNullOrWhiteSpace(text))
                return;
            agent.Prompt = text.Length <= ClaudeTranscriptParser.MaxAgentPromptChars ? text : text[..ClaudeTranscriptParser.MaxAgentPromptChars];
            if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                agent.StartedAt = at;
        }
        catch (JsonException) { }
    }

    /// <summary><c>agent-&lt;id&gt;.meta.json</c> の description（無ければ agentType）。無い・読めなければ null。</summary>
    internal static string? ReadSubagentName(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            foreach (var key in new[] { "description", "name", "agentType" })
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    && v.GetString() is { Length: > 0 } s)
                    return s;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>assistant の本文（text ブロック）。思考（thinking）とツール呼び出しは含めない。本文を持たない行は null。</summary>
    internal static ClaudeReplyText? ReadReply(string line, int lineNumber)
    {
        // 全行を JSON として開くと重いので、本文を持ちうる行だけ開く。
        if (!line.Contains("\"assistant\"", StringComparison.Ordinal) || !line.Contains("\"text\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return null;
            var text = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var body) && body.ValueKind == JsonValueKind.String)
                    text.Append(body.GetString());
            }
            var trimmed = text.ToString().Trim();
            if (trimmed.Length == 0)
                return null;
            DateTimeOffset? at = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            return new ClaudeReplyText(lineNumber, at, trimmed.Length <= MaxReplyChars ? trimmed : trimmed[..MaxReplyChars]);
        }
        catch (JsonException) { return null; }
    }
}
