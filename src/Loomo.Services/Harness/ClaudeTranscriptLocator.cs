using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services;

/// <summary>セッション記録1つ（本体の jsonl と、あればサブエージェントの jsonl 群）。</summary>
public sealed record ClaudeTranscriptFile(string SessionId, string Path, long Length, DateTime LastWriteUtc,
    IReadOnlyList<string> SubagentPaths, long SubagentStamp);

/// <summary>
/// ワークスペースに属する Claude Code のセッション記録を探す（§37.2.1）。
/// <para>記録のフォルダー名（slug）は作業フォルダーの英数字以外を <c>-</c> にしたもの。別のフォルダーが同じ slug に
/// 潰れ得るし、サブフォルダーで起動したセッションは <c>&lt;slug&gt;-sub</c> になるので、名前で当たりを付けたら
/// <b>記録の中の <c>cwd</c> が <see cref="IWorkspaceService.Contains"/> か</b>で確かめる。</para>
/// </summary>
public static class ClaudeTranscriptLocator
{
    /// <summary><c>CLAUDE_CONFIG_DIR</c> があればその下、無ければ <c>~/.claude</c> の <c>projects</c>。</summary>
    public static string DefaultProjectsRoot()
    {
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root = !string.IsNullOrWhiteSpace(config)
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(root, "projects");
    }

    public static string Slug(string folder)
        => Regex.Replace(folder.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");

    /// <summary>ワークスペースのどれかのフォルダー（またはその配下）で起動したセッションの記録フォルダー。</summary>
    public static IReadOnlyList<string> FindProjectDirectories(string projectsRoot, IWorkspaceService workspace)
    {
        if (!Directory.Exists(projectsRoot))
            return [];
        string[] all;
        try { all = Directory.GetDirectories(projectsRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var result = new List<string>();
        foreach (var folder in workspace.Folders)
        {
            var slug = Slug(folder);
            foreach (var dir in all)
            {
                var name = Path.GetFileName(dir);
                var exact = name.Equals(slug, StringComparison.OrdinalIgnoreCase);
                if (!exact && !name.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (result.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    continue;
                var cwd = NewestCwd(dir);
                // cwd が読めない（空のフォルダー等）ときは名前が完全一致したものだけ採る。
                if (cwd is null ? exact : workspace.Contains(cwd))
                    result.Add(dir);
            }
        }
        return result;
    }

    /// <summary>記録フォルダー直下のセッション（<paramref name="sinceUtc"/> 以降に書かれたもの、新しい順）。</summary>
    public static IReadOnlyList<ClaudeTranscriptFile> ListSessions(string projectDirectory, DateTime sinceUtc)
    {
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(projectDirectory).GetFiles("*.jsonl"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var result = new List<ClaudeTranscriptFile>();
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc < sinceUtc)
                continue;
            var sessionId = Path.GetFileNameWithoutExtension(file.Name);
            var subDir = Path.Combine(projectDirectory, sessionId, "subagents");
            var subs = new List<string>();
            long stamp = 0;
            if (Directory.Exists(subDir))
            {
                try
                {
                    foreach (var sub in new DirectoryInfo(subDir).GetFiles("*.jsonl").OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        subs.Add(sub.FullName);
                        stamp += sub.Length ^ sub.LastWriteTimeUtc.Ticks;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            result.Add(new ClaudeTranscriptFile(sessionId, file.FullName, file.Length, file.LastWriteTimeUtc, subs, stamp));
        }
        return result.OrderByDescending(f => f.LastWriteUtc).ToList();
    }

    /// <summary>Claude Code が書き込み中でも読めるように開いて、行を返す。</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string? NewestCwd(string dir)
    {
        try
        {
            var newest = new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest is null ? null : ClaudeTranscriptParser.ReadCwd(ReadLines(newest.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
