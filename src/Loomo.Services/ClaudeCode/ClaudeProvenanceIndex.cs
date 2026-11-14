using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 出自の索引（§43）：このワークスペースの Claude Code の記録から「どのファイルへ・どの頼みごとで・何を書いたか」を
/// 引く。コミットしていない行は長く残らないので、見るのは最近の記録だけ（<see cref="Window"/>・<see cref="MaxSessions"/>）。
/// <para>記録 1 つは、長さと更新時刻が変わらない限り 1 回しか読まない。読むのは呼んだスレッド（エディタの blame は
/// 背景から呼ぶ）——UI スレッドから呼ばないこと。記録の読み方は <see cref="ClaudeTranscriptParser"/> のまま
/// （頼みごとの見分け・拒否と失敗の除外を二重に持たない）。足すのは書いた中身を読むことだけ。</para>
/// </summary>
public sealed class ClaudeProvenanceIndex
{
    /// <summary>この期間に書かれた記録だけを見る。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    /// <summary>見る記録の数の上限（新しい順）。</summary>
    public const int MaxSessions = 24;

    private static readonly HashSet<string> WritingTools = new(StringComparer.Ordinal)
    {
        "Edit", "MultiEdit", "Write", "NotebookEdit",
    };

    private readonly IWorkspaceService _workspace;
    private readonly Func<string> _projectsRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClaudeWrite> _handles = new();
    private readonly Dictionary<string, Dictionary<(string Hash, int OriginalLine), ClaudeWrite>> _commitLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ClaudeWrite, int> _handleOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>結んだ出自の置き場（§43.6 A2b）。無ければ覚えない（記録が消えたら線も消える）。</summary>
    public IClaudeProvenanceStore? Store { get; }

    public ClaudeProvenanceIndex(IWorkspaceService workspace, Func<string>? projectsRoot = null, IClaudeProvenanceStore? store = null)
    {
        Store = store;
        _workspace = workspace;
        _projectsRoot = projectsRoot ?? ClaudeTranscriptLocator.DefaultProjectsRoot;
    }

    /// <summary><paramref name="filePath"/> への書き込み（古い順）。</summary>
    public IReadOnlyList<ClaudeWrite> WritesFor(string filePath)
    {
        var key = Normalize(filePath);
        lock (_gate)
        {
            Refresh();
            return _sessions.Values
                .SelectMany(entry => entry.Writes.TryGetValue(key, out var writes) ? writes : [])
                .OrderBy(write => write.At ?? DateTimeOffset.MinValue)
                .ToList();
        }
    }

    /// <summary>同じ頼みごと（同じ記録の同じ行から始まるターン）が書いたもの全部（古い順）。出自の逆向き——
    /// 「この頼みごとで、ほかにどこが変わったか」。すでに読んである記録から引くだけで、読み直さない。</summary>
    public IReadOnlyList<ClaudeWrite> WritesOfRequest(ClaudeWrite write)
    {
        lock (_gate)
        {
            return _sessions.Values
                .SelectMany(entry => entry.Writes.Values.SelectMany(list => list))
                .Where(other => other.PromptLine == write.PromptLine
                                && string.Equals(other.SessionId, write.SessionId, StringComparison.Ordinal))
                .OrderBy(other => other.At ?? DateTimeOffset.MinValue)
                .ToList();
        }
    }

    /// <summary>記録のセッションと頼みごとの行番号から、その頼みごとが書いたもの全部（古い順）。ターミナルサポートの
    /// ターンから引く入口（§43.6 A3）。記録を読み直すので、UI スレッドから呼ばないこと。</summary>
    public IReadOnlyList<ClaudeWrite> WritesOfRequest(string sessionId, int promptLine)
    {
        lock (_gate)
        {
            Refresh();
            return _sessions.Values
                .SelectMany(entry => entry.Writes.Values.SelectMany(list => list))
                .Where(write => write.PromptLine == promptLine && string.Equals(write.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(write => write.At ?? DateTimeOffset.MinValue)
                .ToList();
        }
    }

    /// <summary>コミットしていない行（0 始まり → 中身）を、それを書いた頼みごとへ結ぶ。</summary>
    public Dictionary<int, ClaudeWrite> Attribute(string filePath, IReadOnlyDictionary<int, string> uncommitted)
        => uncommitted.Count == 0 ? [] : ClaudeProvenance.Attribute(uncommitted, WritesFor(filePath));

    /// <summary>書き込みを整数 1 つで指す（エディタの blame の行は文字列と整数しか運べない）。同じ書き込みには同じ値。</summary>
    public int HandleOf(ClaudeWrite write)
    {
        lock (_gate)
        {
            if (_handleOf.TryGetValue(write, out var handle))
                return handle;
            _handles.Add(write);
            return _handleOf[write] = _handles.Count; // 1 始まり（0 は「無い」）
        }
    }

    public ClaudeWrite? FromHandle(int handle)
    {
        lock (_gate)
            return handle >= 1 && handle <= _handles.Count ? _handles[handle - 1] : null;
    }

    /// <summary>
    /// コミット済みの行の出自を覚える（§43.6 A2a）。blame の行はハッシュと元の行番号を git のまま運ぶので、取っ手を
    /// 載せる欄が無い——代わりに（ファイル・短いハッシュ・そのコミットでの行番号）で引けるようにする。
    /// ファイルごとに、blame を引くたび丸ごと差し替える。
    /// </summary>
    public void SetCommitLines(string filePath, Dictionary<(string Hash, int OriginalLine), ClaudeWrite> lines)
    {
        var key = Normalize(filePath);
        lock (_gate)
        {
            if (lines.Count == 0)
                _commitLines.Remove(key);
            else
                _commitLines[key] = lines;
        }
    }

    /// <summary>コミット済みの行（blame の行のハッシュと元の行番号）を書いた頼みごと。結んでいなければ null。</summary>
    public ClaudeWrite? ForCommitLine(string? filePath, string? hash, int originalLine)
    {
        if (filePath is not { Length: > 0 } || hash is not { Length: > 0 })
            return null;
        var key = Normalize(filePath);
        lock (_gate)
            return _commitLines.TryGetValue(key, out var lines) && lines.TryGetValue((hash, originalLine), out var write) ? write : null;
    }

    private void Refresh()
    {
        var since = DateTime.UtcNow - Window;
        var files = ClaudeTranscriptLocator.FindProjectDirectories(_projectsRoot(), _workspace)
            .SelectMany(dir => ClaudeTranscriptLocator.ListSessions(dir, since))
            .OrderByDescending(file => file.LastWriteUtc)
            .Take(MaxSessions)
            .ToList();

        var alive = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _sessions.Keys.Where(path => !alive.Contains(path)).ToList())
            _sessions.Remove(gone);

        foreach (var file in files)
        {
            if (_sessions.TryGetValue(file.Path, out var known)
                && known.Length == file.Length && known.LastWriteUtc == file.LastWriteUtc && known.SubagentStamp == file.SubagentStamp)
                continue;
            _sessions[file.Path] = new Entry(file.Length, file.LastWriteUtc, file.SubagentStamp, ReadWrites(file));
        }
    }

    /// <summary>記録 1 つ（とそのサブエージェント）から、ファイルごとの書き込みを読む。</summary>
    internal static Dictionary<string, List<ClaudeWrite>> ReadWrites(ClaudeTranscriptFile file)
    {
        var result = new Dictionary<string, List<ClaudeWrite>>(StringComparer.OrdinalIgnoreCase);
        HarnessRun run;
        try
        {
            run = ClaudeTranscriptParser.Parse(file.SessionId, file.Path, ClaudeTranscriptLocator.ReadLines(file.Path),
                file.SubagentPaths.Select(path => (path, ClaudeTranscriptLocator.ReadLines(path))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }

        // 適用された書き込みだけ（結果が返っていて、失敗でも人間の拒否でもない）。
        var calls = run.ToolCalls
            .Where(call => WritingTools.Contains(call.Tool) && call.FilePath is { Length: > 0 } && call.ToolUseId is not null
                           && call.HasResult && !call.IsError && !call.IsRejected)
            .ToList();
        foreach (var group in calls.GroupBy(call => call.TranscriptPath, StringComparer.OrdinalIgnoreCase))
        {
            var wanted = group.Select(call => call.LineNumber).ToHashSet();
            var raw = ReadLinesAt(group.Key, wanted);
            foreach (var call in group)
            {
                if (!raw.TryGetValue(call.LineNumber, out var line)
                    // 行を足さない書き込み（消すだけの Edit）も持つ。行の出自には効かないが、頼みごと単位の差分（§43.6 A3）は
                    // 操作を順に当て直すので、抜けると「後」が合わない。
                    || ClaudeProvenance.ReadAddedLines(line, call.ToolUseId!) is not { } added
                    || PromptOf(run, call) is not { } owner)
                    continue;
                var (prompt, promptLine) = owner;
                var path = Normalize(call.FilePath!);
                if (!result.TryGetValue(path, out var list))
                    result[path] = list = new List<ClaudeWrite>();
                list.Add(new ClaudeWrite(file.SessionId, file.Path, path, prompt, promptLine, call.At, added, call.FromSubagent)
                {
                    SourcePath = call.TranscriptPath,
                    SourceLine = call.LineNumber,
                    ResultLine = call.ResultLineNumber,
                    ToolUseId = call.ToolUseId,
                });
            }
        }
        return result;
    }

    /// <summary>その操作が応えていた頼みごと。本体の操作は行番号で、サブエージェントの操作（別ファイル）は時刻で決める
    /// ——<see cref="ClaudeSessionDigest"/> のターンの区切りと同じ。</summary>
    private static (string Prompt, int Line)? PromptOf(HarnessRun run, RunToolCall call)
    {
        var marks = run.PromptMarks;
        for (var i = Math.Min(marks.Count, run.Prompts.Count) - 1; i >= 0; i--)
        {
            var before = call.FromSubagent
                ? call.At is { } at && (marks[i].At is not { } started || started <= at)
                : marks[i].LineNumber < call.LineNumber;
            if (before)
                return (run.Prompts[i], marks[i].LineNumber);
        }
        return null;
    }

    private static Dictionary<int, string> ReadLinesAt(string path, HashSet<int> lineNumbers)
    {
        var result = new Dictionary<int, string>();
        try
        {
            var number = 0;
            foreach (var line in ClaudeTranscriptLocator.ReadLines(path))
            {
                if (lineNumbers.Contains(++number))
                    result[number] = line;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private sealed record Entry(long Length, DateTime LastWriteUtc, long SubagentStamp, Dictionary<string, List<ClaudeWrite>> Writes);
}
