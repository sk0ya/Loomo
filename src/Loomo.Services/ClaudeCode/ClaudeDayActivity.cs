using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// Claude Code の 1 ターン（頼みごと 1 つと、それに応えた操作）を時刻の上の区間として（§43.6 C）。
/// <see cref="End"/> は最後の操作の時刻（操作が無ければ <see cref="Start"/>）。
/// </summary>
public sealed record ClaudeTurnSpan(
    string SessionId, string TranscriptPath, int PromptLine, string Prompt,
    DateTimeOffset Start, DateTimeOffset End, int FilesWritten, int ToolCalls)
{
    public string PromptHead => ClaudeProvenance.Head(Prompt);
}

/// <summary>
/// その日に Claude Code が部屋でしたこと（§43.6 C：軌跡の二声化）。このワークスペースの記録から、頼みごとごとの区間と
/// 書き換えたファイルの数を引く。軌跡（<c>trail.db</c>）には書かない——軌跡の点は人間の移動だけのもの（§27.1）で、
/// Claude の側は記録（<c>~/.claude/projects</c>）が正本。ターンの区切りはターミナルサポート（§39.4）・出自（§43.3）と同じ
/// （<see cref="ClaudeTranscriptParser"/> の頼みごとの印）なので、数え方は 1 つ。
/// <para>記録 1 つは、長さと更新時刻が変わらない限り 1 回しか読まない。読むのは呼んだスレッド——UI スレッドから呼ばないこと。</para>
/// </summary>
public sealed class ClaudeDayActivity
{
    /// <summary>1 日ぶんで読む記録の数の上限（その日に近いものから）。</summary>
    public const int MaxSessions = 40;

    private static readonly HashSet<string> WritingTools = new(StringComparer.Ordinal)
    {
        "Edit", "MultiEdit", "Write", "NotebookEdit",
    };

    private readonly IWorkspaceService _workspace;
    private readonly Func<string> _projectsRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeDayActivity(IWorkspaceService workspace, Func<string>? projectsRoot = null)
    {
        _workspace = workspace;
        _projectsRoot = projectsRoot ?? ClaudeTranscriptLocator.DefaultProjectsRoot;
    }

    /// <summary><paramref name="day"/>（ローカルの日付）に始まったターン（古い順）。</summary>
    public IReadOnlyList<ClaudeTurnSpan> Read(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var dayEnd = dayStart.AddDays(1);
        lock (_gate)
        {
            // その日より前に書き終わった記録に、その日のターンは無い。あとまで続いた記録（再開した長い会話）には有り得るので、
            // その日に近いものから上限まで見る。
            var files = ClaudeTranscriptLocator.FindProjectDirectories(_projectsRoot(), _workspace)
                .SelectMany(dir => ClaudeTranscriptLocator.ListSessions(dir, dayStart.ToUniversalTime()))
                .OrderBy(file => file.LastWriteUtc)
                .Take(MaxSessions)
                .ToList();
            var result = new List<ClaudeTurnSpan>();
            foreach (var file in files)
            {
                if (!_sessions.TryGetValue(file.Path, out var known)
                    || known.Length != file.Length || known.LastWriteUtc != file.LastWriteUtc || known.SubagentStamp != file.SubagentStamp)
                    _sessions[file.Path] = known = new Entry(file.Length, file.LastWriteUtc, file.SubagentStamp, ReadTurns(file));
                result.AddRange(known.Turns.Where(turn =>
                {
                    var local = turn.Start.LocalDateTime;
                    return local >= dayStart && local < dayEnd;
                }));
            }
            return result.OrderBy(turn => turn.Start).ToList();
        }
    }

    /// <summary>記録 1 つ（とそのサブエージェント）をターンの区間にする。時刻の無い頼みごとは軸に置けないので落とす。</summary>
    internal static List<ClaudeTurnSpan> ReadTurns(ClaudeTranscriptFile file)
    {
        var turns = new List<ClaudeTurnSpan>();
        HarnessRun run;
        try
        {
            run = ClaudeTranscriptParser.Parse(file.SessionId, file.Path, ClaudeTranscriptLocator.ReadLines(file.Path),
                file.SubagentPaths.Select(path => (path, ClaudeTranscriptLocator.ReadLines(path))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return turns; }

        var marks = run.PromptMarks;
        var count = Math.Min(marks.Count, run.Prompts.Count);
        for (var i = 0; i < count; i++)
        {
            if (marks[i].At is not { } start)
                continue;
            var nextLine = i + 1 < count ? marks[i + 1].LineNumber : int.MaxValue;
            var nextAt = i + 1 < count ? marks[i + 1].At : null;
            // 本体の操作は行番号で、サブエージェントの操作（別ファイル）は時刻で、このターンのものか決める（§39.4・§43.3 と同じ）。
            var calls = run.ToolCalls.Where(call => call.FromSubagent
                ? call.At is { } at && at >= start && (nextAt is not { } until || at < until)
                : call.LineNumber > marks[i].LineNumber && call.LineNumber < nextLine).ToList();
            var end = calls.Select(call => call.At).OfType<DateTimeOffset>().DefaultIfEmpty(start).Max();
            var written = calls
                .Where(call => WritingTools.Contains(call.Tool) && call.FilePath is { Length: > 0 }
                               && call.HasResult && !call.IsError && !call.IsRejected)
                .Select(call => call.FilePath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            turns.Add(new ClaudeTurnSpan(file.SessionId, file.Path, marks[i].LineNumber, run.Prompts[i],
                start, end < start ? start : end, written, calls.Count));
        }
        return turns;
    }

    private sealed record Entry(long Length, DateTime LastWriteUtc, long SubagentStamp, List<ClaudeTurnSpan> Turns);
}
