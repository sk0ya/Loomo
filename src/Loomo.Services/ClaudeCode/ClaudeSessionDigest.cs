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
    DateTimeOffset? At,
    IReadOnlyList<RunToolCall> Calls,
    IReadOnlyList<ClaudeTouchedFile> Files,
    IReadOnlyList<string> Replies,
    DateTimeOffset? LastAt)
{
    /// <summary>このターンの最後の本文。ターンが終わっていれば Claude の返事、作業中なら途中のメモ。</summary>
    public string? Reply => Replies.Count > 0 ? Replies[^1] : null;

    /// <summary>シェルのコマンドを走らせた回数。</summary>
    public int Commands => Calls.Count(c => c.Command is not null);

    /// <summary>失敗した操作（人間の拒否は含まない）。</summary>
    public IEnumerable<RunToolCall> Failures => Calls.Where(c => c.IsError);
}

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

    /// <summary>返事の本文を持つ上限（字）。面に出すのは冒頭だけなので、長い返事を丸ごと抱えない。</summary>
    public const int MaxReplyChars = 2000;

    /// <summary>いまのターン（最後の頼みごと）。頼みごとがまだ無ければ null。</summary>
    public ClaudeTurn? CurrentTurn => Turns.Count > 0 ? Turns[^1] : null;

    /// <summary>ファイルを書き換えるツール。<c>Read</c> は触ったうちに入れない（読んだだけのファイルを並べると、
    /// 「何が変わったか」を見に来た人の目が埋もれる）。</summary>
    private static readonly HashSet<string> WritingTools = new(StringComparer.Ordinal)
    {
        "Edit", "MultiEdit", "Write", "NotebookEdit",
    };

    /// <summary>記録ファイルを読んで畳む。書き込み中でも読める（<see cref="ClaudeTranscriptLocator.ReadLines"/>）。</summary>
    public static ClaudeSessionDigest? Read(string transcriptPath)
    {
        if (!File.Exists(transcriptPath))
            return null;
        var lines = ClaudeTranscriptLocator.ReadLines(transcriptPath).ToList();
        return Build(Path.GetFileNameWithoutExtension(transcriptPath), transcriptPath,
            File.GetLastWriteTimeUtc(transcriptPath), lines);
    }

    /// <summary>行の列から畳む（I/O なし。テストは文字列で書ける）。</summary>
    public static ClaudeSessionDigest Build(string sessionId, string transcriptPath, DateTime lastWriteUtc, IReadOnlyList<string> lines)
    {
        var run = ClaudeTranscriptParser.Parse(sessionId, transcriptPath, lines);
        var summary = ClaudeSessionCatalog.Summarize(sessionId, transcriptPath, lastWriteUtc, lines);
        var replies = ReadReplies(lines);

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
            var lastAt = new[] { calls.LastOrDefault()?.At, texts.LastOrDefault().At }.Max();
            turns.Add(new ClaudeTurn(run.Prompts[i], start.At, calls, TouchedFiles(calls),
                texts.Select(r => r.Text!).ToList(), lastAt));
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

    /// <summary>assistant の本文（text ブロック）を行番号つきで。思考（thinking）とツール呼び出しは含めない。</summary>
    private static List<(int Line, DateTimeOffset? At, string? Text)> ReadReplies(IReadOnlyList<string> lines)
    {
        var result = new List<(int, DateTimeOffset?, string?)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // 全行を JSON として開くと重いので、本文を持ちうる行だけ開く。
            if (!line.Contains("\"assistant\"", StringComparison.Ordinal) || !line.Contains("\"text\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                    || !root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                    continue;
                var text = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && block.TryGetProperty("text", out var body) && body.ValueKind == JsonValueKind.String)
                        text.Append(body.GetString());
                }
                var trimmed = text.ToString().Trim();
                if (trimmed.Length == 0)
                    continue;
                DateTimeOffset? at = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                result.Add((i + 1, at, trimmed.Length <= MaxReplyChars ? trimmed : trimmed[..MaxReplyChars]));
            }
            catch (JsonException) { }
        }
        return result;
    }
}
