using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 頼みごと単位の差分の 1 ファイルぶん（§43.6 A3）。<see cref="Before"/>／<see cref="After"/> は改行を LF にそろえた中身。
/// </summary>
/// <param name="Exact">ファイル全体の前後を組めた（行番号はそのときのファイルのもの）。false は抜粋——前の中身が
/// 分からない・操作が当たらなかったので、操作ごとの置換前と置換後だけを並べたもの。</param>
/// <param name="Created">この頼みごとが作ったファイル（前は無かった）。</param>
/// <param name="AddedLines">この頼みごとが足した、中身のある行の数。</param>
/// <param name="AddedLinesPresent">そのうち、いまのファイルに残っている数。</param>
/// <param name="Current">いまのファイルが「頼みごとの後」と比べてどうか。</param>
public sealed record ClaudeRequestFileDiff(
    string FilePath, string Before, string After, bool Exact, bool Created, int Edits,
    int AddedLines, int AddedLinesPresent, ClaudeRequestFileState Current);

/// <summary>頼みごとの後、そのファイルがどうなったか。</summary>
public enum ClaudeRequestFileState
{
    /// <summary>頼みごとの後のまま。</summary>
    Unchanged,
    /// <summary>あとから変わっている（別の頼みごと・人間・ツールを通さない変更）。</summary>
    ChangedLater,
    /// <summary>もう無い。</summary>
    Missing,
    /// <summary>抜粋なので比べられない。</summary>
    Unknown,
}

/// <summary>
/// 頼みごと単位の差分を組む（§43.6 A3）。<b>前の中身</b>は次の順で探す：
/// <list type="number">
/// <item>Claude Code のファイル履歴（<c>~/.claude/file-history/&lt;セッション&gt;/</c>）。頼みごとのメッセージに結ばれた
///   バックアップが、その頼みごとで最初に触る前の中身。バックアップ無しと記録されていれば「無かった」（新しいファイル）。</item>
/// <item>最初の書き込みの結果に残っている元の中身（<c>originalFile</c>）・新規作成の印（<c>type: create</c>）。</item>
/// <item>どれも無ければ抜粋（<see cref="ClaudeRequestDiff.Excerpt"/>）。</item>
/// </list>
/// <b>後の中身</b>は前の中身に操作を当て直して作る（いまのファイルは、あとの変更が混じるので使わない）。
/// ファイル履歴と結果の形は Claude Code の私的な形式なので、読めなければ黙って次の手へ落ちる。
/// 記録を読むので、UI スレッドから呼ばないこと。
/// </summary>
public static class ClaudeRequestDiffBuilder
{
    /// <summary>1 ファイルの中身として持つ上限。巨大な生成物は抜粋にも載せない。</summary>
    private const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>ファイル履歴の置き場所（記録フォルダーの隣）。</summary>
    public static string DefaultFileHistoryRoot()
        => Path.Combine(Path.GetDirectoryName(ClaudeTranscriptLocator.DefaultProjectsRoot()) ?? "", "file-history");

    /// <param name="writes">同じ頼みごとの書き込み（<see cref="ClaudeProvenanceIndex.WritesOfRequest"/>）。</param>
    public static IReadOnlyList<ClaudeRequestFileDiff> Build(IReadOnlyList<ClaudeWrite> writes, string? fileHistoryRoot = null)
    {
        var usable = writes.Where(write => write.SourcePath is { Length: > 0 } && write.SourceLine > 0 && write.ToolUseId is not null).ToList();
        if (usable.Count == 0)
            return [];
        var origin = usable[0];
        var history = ReadHistory(origin, fileHistoryRoot ?? DefaultFileHistoryRoot());

        // 記録の行を、要るぶんだけ読む（操作の行と結果の行）。
        var raw = new Dictionary<(string Path, int Line), string>();
        foreach (var group in usable.GroupBy(write => write.SourcePath!, StringComparer.OrdinalIgnoreCase))
        {
            var wanted = group.SelectMany(write => new[] { write.SourceLine, write.ResultLine }).Where(line => line > 0).ToHashSet();
            foreach (var (number, line) in ReadLinesAt(group.Key, wanted))
                raw[(group.Key.ToUpperInvariant(), number)] = line;
        }
        string? Line(ClaudeWrite write, int number)
            => number > 0 && raw.TryGetValue((write.SourcePath!.ToUpperInvariant(), number), out var line) ? line : null;

        var result = new List<ClaudeRequestFileDiff>();
        foreach (var file in usable.GroupBy(write => write.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = file.OrderBy(write => write.At ?? DateTimeOffset.MinValue).ThenBy(write => write.SourceLine).ToList();
            var operations = new List<ClaudeEditOp>();
            var readable = true;
            foreach (var write in ordered)
            {
                if (Line(write, write.SourceLine) is { } line && ClaudeRequestDiff.ReadOperations(line, write.ToolUseId!) is { } ops)
                    operations.AddRange(ops);
                else
                    readable = false; // NotebookEdit・読めない行。全体は組まない。
            }
            if (operations.Count == 0)
                continue;

            var (before, created) = BeforeOf(file.Key, history, Line(ordered[0], ordered[0].ResultLine));
            var after = readable && before is not null ? ClaudeRequestDiff.Replay(before, operations) : null;
            var added = ordered.SelectMany(write => write.Lines).Where(ClaudeProvenance.IsDistinctive).Distinct(StringComparer.Ordinal).ToList();
            var current = ReadText(file.Key);
            var present = 0;
            if (current is not null && added.Count > 0)
            {
                var lines = new HashSet<string>(current.Split('\n').Select(line => line.TrimEnd()), StringComparer.Ordinal);
                present = added.Count(lines.Contains);
            }

            if (after is not null)
            {
                var state = current is null ? ClaudeRequestFileState.Missing
                    : string.Equals(current, after, StringComparison.Ordinal) ? ClaudeRequestFileState.Unchanged
                    : ClaudeRequestFileState.ChangedLater;
                result.Add(new ClaudeRequestFileDiff(file.Key, ClaudeRequestDiff.Normalize(before), after, true, created,
                    ordered.Count, added.Count, present, state));
            }
            else
            {
                var (left, right) = ClaudeRequestDiff.Excerpt(operations);
                result.Add(new ClaudeRequestFileDiff(file.Key, left, right, false, false, ordered.Count, added.Count, present,
                    current is null ? ClaudeRequestFileState.Missing : ClaudeRequestFileState.Unknown));
            }
        }
        return result;
    }

    /// <summary>頼みごとの前の中身。分からなければ null。</summary>
    private static (string? Before, bool Created) BeforeOf(string filePath, History history, string? firstResultLine)
    {
        if (history.Backups.TryGetValue(filePath, out var backup))
        {
            if (backup is null)
                return ("", true); // バックアップ無し＝触る前は無かった
            if (ReadText(backup) is { } text)
                return (text, false);
        }
        if (firstResultLine is null)
            return (null, false);
        try
        {
            using var doc = JsonDocument.Parse(firstResultLine);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("toolUseResult", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (Str(result, "originalFile") is { } original)
                    return (ClaudeRequestDiff.Normalize(original), false);
                if (Str(result, "type") == "create")
                    return ("", true);
            }
        }
        catch (JsonException) { }
        return (null, false);
    }

    private sealed record History(Dictionary<string, string?> Backups);

    /// <summary>
    /// 本体の記録から、頼みごとのメッセージに結ばれたファイル履歴を読む。2 つの形がある：
    /// <c>file-history-snapshot</c>（<c>messageId</c>・<c>snapshot.trackedFileBackups</c>＝パス → バックアップ）と
    /// <c>file-history-delta</c>（<c>snapshotMessageId</c>・<c>trackingPath</c>・<c>backup</c>）。
    /// 同じファイルに複数あれば、版（<c>version</c>）のいちばん小さいもの＝その頼みごとで最初に取ったもの。
    /// </summary>
    private static History ReadHistory(ClaudeWrite origin, string fileHistoryRoot)
    {
        var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string MessageId, string Path, string? Backup, int Version, string? ParentDir)>();
        string? promptId = null;
        string? cwd = null;
        try
        {
            var number = 0;
            foreach (var line in ClaudeTranscriptLocator.ReadLines(origin.TranscriptPath))
            {
                number++;
                var isPrompt = number == origin.PromptLine;
                if (!isPrompt && !line.Contains("\"file-history-", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    if (isPrompt)
                    {
                        promptId = Str(root, "uuid");
                        cwd = Str(root, "cwd");
                    }
                    switch (Str(root, "type"))
                    {
                        case "file-history-snapshot" when root.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object
                            && snapshot.TryGetProperty("trackedFileBackups", out var tracked) && tracked.ValueKind == JsonValueKind.Object:
                            // 途中で足された分（isSnapshotUpdate）は外側と内側の ID が違うことがある。どちらでも引けるようにする。
                            var ids = new[] { Str(root, "messageId"), Str(snapshot, "messageId") }
                                .Where(id => id is { Length: > 0 }).Distinct(StringComparer.Ordinal).ToList();
                            foreach (var item in tracked.EnumerateObject())
                            {
                                if (item.Value.ValueKind != JsonValueKind.Object)
                                    continue;
                                foreach (var id in ids)
                                    entries.Add((id!, item.Name, Str(item.Value, "backupFileName"), Int(item.Value, "version"), null));
                            }
                            break;
                        case "file-history-delta" when root.TryGetProperty("backup", out var backup) && backup.ValueKind == JsonValueKind.Object
                            && Str(root, "trackingPath") is { Length: > 0 } tracking:
                            entries.Add((Str(root, "snapshotMessageId") ?? "", tracking, Str(backup, "backupFileName"),
                                Int(backup, "version"), Str(backup, "realParentDir")));
                            break;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (promptId is null)
            return new History(backups);
        foreach (var entry in entries.Where(entry => entry.MessageId == promptId))
        {
            if (Resolve(entry.Path, entry.ParentDir, cwd) is not { } path
                || (versions.TryGetValue(path, out var known) && known <= entry.Version))
                continue;
            versions[path] = entry.Version;
            // バックアップの名前は記録から来る文字列。フォルダーの外へ出る形は読まない。
            backups[path] = entry.Backup is { Length: > 0 } name && name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Contains("..", StringComparison.Ordinal)
                ? Path.Combine(fileHistoryRoot, origin.SessionId, name)
                : entry.Backup is null ? null : "";
        }
        return new History(backups);
    }

    private static string? Resolve(string path, string? parentDir, string? cwd)
    {
        try
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            if (parentDir is { Length: > 0 })
                return Path.GetFullPath(Path.Combine(parentDir, Path.GetFileName(path)));
            return cwd is { Length: > 0 } ? Path.GetFullPath(Path.Combine(cwd, path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? ReadText(string path)
    {
        try
        {
            if (path.Length == 0)
                return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                return null;
            return ClaudeRequestDiff.Normalize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
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

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
