using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services;

/// <summary>ハーネスペインが一度に描く材料（§37）。</summary>
public sealed record HarnessSnapshot(
    HarnessDefinition Definition,
    IReadOnlyList<HarnessRun> Runs,
    IReadOnlyList<HarnessCandidate> Candidates,
    IReadOnlyDictionary<string, HarnessVerdict> Verdicts,
    IReadOnlyDictionary<string, HarnessVersion?> VersionByRun,
    IReadOnlyList<string> TranscriptDirectories,
    DateTimeOffset Since)
{
    public HarnessVersion? VersionOf(HarnessRun run) => VersionByRun.TryGetValue(run.Id, out var v) ? v : null;
}

/// <summary>
/// ワークスペースのハーネス定義・Claude Code の記録・git の版・人間の判定を集めて <see cref="HarnessSnapshot"/> にする。
/// <para><b>解析結果はファイルの長さ＋更新時刻をキーにメモリへ持つ</b>。記録は合計で数百 MB になり得るので、
/// 期間の切り替えや更新のたびに全部を読み直さない（書き足されたセッションだけ読み直す）。</para>
/// <para>記録の解析は CPU 仕事なので <see cref="Task.Run(Action)"/>、git は子プロセスなので
/// <see cref="GitCommandRunner"/>（＝<c>ChildProcessIo</c>、§31.16）を通す。</para>
/// </summary>
public sealed class HarnessAnalyzer
{
    /// <summary>ハーネスとして版を数えるパス（§37.6）。</summary>
    public static readonly string[] HarnessPaths = [".harness", "CLAUDE.md", "AGENTS.md", ".claude"];

    private readonly IWorkspaceService _workspace;
    private readonly string _projectsRoot;
    private readonly GitCommandRunner _git;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Write, long SubStamp, HarnessRun Run)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public HarnessAnalyzer(IWorkspaceService workspace)
        : this(workspace, ClaudeTranscriptLocator.DefaultProjectsRoot())
    {
    }

    public HarnessAnalyzer(IWorkspaceService workspace, string projectsRoot)
    {
        _workspace = workspace;
        _projectsRoot = projectsRoot;
        _git = new GitCommandRunner(new GitRootState(workspace));
    }

    public async Task<HarnessSnapshot> AnalyzeAsync(
        TimeSpan period, IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        var folders = _workspace.Folders.ToList();
        var since = DateTimeOffset.UtcNow - period;

        var (definition, directories, runs, verdicts) = await Task.Run(() =>
        {
            var definition = HarnessDefinitionReader.Read(folders);
            var directories = ClaudeTranscriptLocator.FindProjectDirectories(_projectsRoot, _workspace);
            var files = directories.SelectMany(d => ClaudeTranscriptLocator.ListSessions(d, since.UtcDateTime)).ToList();
            EvictOutside(directories);
            var runs = new List<HarnessRun>(files.Count);
            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((i, files.Count));
                // 記録フォルダーは slug で選んでいるので、同じ slug に潰れた別フォルダーのセッションを Run ごとに cwd で弾く。
                if (ParseCached(files[i]) is { } run && (run.Prompts.Count > 0 || run.ToolCalls.Count > 0)
                    && (run.Cwd is null || _workspace.Contains(run.Cwd))
                    && (run.EndedAt ?? run.StartedAt ?? DateTimeOffset.MaxValue) >= since)
                    runs.Add(run);
            }
            progress?.Report((files.Count, files.Count));
            runs.Sort((a, b) => Nullable.Compare(b.StartedAt, a.StartedAt));
            var verdicts = new Dictionary<string, HarnessVerdict>(StringComparer.Ordinal);
            foreach (var folder in folders)
            {
                foreach (var (id, v) in HarnessVerdictStore.Load(HarnessVerdictStore.PathFor(folder)))
                    verdicts[id] = v;
            }
            return (definition, directories, runs, verdicts);
        }, cancellationToken).ConfigureAwait(false);

        var timelines = new Dictionary<string, HarnessVersionTimeline>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            timelines[folder] = await LoadTimelineAsync(folder, cancellationToken).ConfigureAwait(false);

        var versionByRun = new Dictionary<string, HarnessVersion?>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            // Run の版はその Run を起動したフォルダーの履歴から引く（マルチルートでは履歴がフォルダーごとに違う）。
            var folder = _workspace.FolderForOrPrimary(run.Cwd);
            versionByRun[run.Id] = folder is not null && run.StartedAt is { } at && timelines.TryGetValue(folder, out var t)
                ? t.VersionAt(at)
                : null;
        }

        var candidates = await Task.Run(() => HarnessDetectors.Detect(runs), cancellationToken).ConfigureAwait(false);
        return new HarnessSnapshot(definition, runs, candidates, verdicts, versionByRun, directories, since);
    }

    /// <summary>
    /// 今のワークスペースの記録フォルダーに無いセッションをキャッシュから捨てる。Run はツール呼び出しを全部持つので、
    /// ワークスペースを渡り歩くたびに積むとプロセスの寿命ぶん膨らむ。期間の外のものは残す（期間を戻したとき読み直さない）。
    /// </summary>
    private void EvictOutside(IReadOnlyList<string> directories)
    {
        foreach (var path in _cache.Keys)
        {
            var dir = Path.GetDirectoryName(path);
            if (!directories.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                _cache.TryRemove(path, out _);
        }
    }

    private HarnessRun? ParseCached(ClaudeTranscriptFile file)
    {
        if (_cache.TryGetValue(file.Path, out var cached)
            && cached.Length == file.Length && cached.Write == file.LastWriteUtc && cached.SubStamp == file.SubagentStamp)
            return cached.Run;
        try
        {
            var run = ClaudeTranscriptParser.Parse(
                file.SessionId, file.Path, ClaudeTranscriptLocator.ReadLines(file.Path),
                file.SubagentPaths.Select(p => (p, ClaudeTranscriptLocator.ReadLines(p))));
            _cache[file.Path] = (file.Length, file.LastWriteUtc, file.SubagentStamp, run);
            return run;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // 消された・ロックされた記録は飛ばす（次の更新で拾い直す）
        }
    }

    private async Task<HarnessVersionTimeline> LoadTimelineAsync(string folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
            return HarnessVersionTimeline.Empty;
        var args = new List<string> { "log", "--format=%H%x1f%ct%x1f%s", "--" };
        args.AddRange(HarnessPaths);
        var result = await _git.RunInAsync(folder, null, TimeSpan.FromSeconds(20), cancellationToken, args.ToArray())
            .ConfigureAwait(false);
        return result.Success ? HarnessVersionTimeline.ParseGitLog(result.Output) : HarnessVersionTimeline.Empty;
    }
}
