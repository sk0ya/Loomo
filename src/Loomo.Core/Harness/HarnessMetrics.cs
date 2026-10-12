using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// Run の集まりの指標（§37.4）。<see cref="TokensPerAccepted"/> は<b>失敗・差し戻し・未判定を含む</b>総トークンを
/// 受け入れ件数で割る——成功した Run だけの平均にすると、失敗に払ったぶんが消える（構想§10）。
/// </summary>
public sealed record HarnessSummary(
    int RunCount,
    int Accepted,
    int Rejected,
    RunTokenUsage Usage,
    int ToolCalls,
    int ToolErrors,
    int CostKnownRuns,
    decimal CostTotal)
{
    /// <summary>受け入れ0件なら null（値を作らない）。</summary>
    public long? TokensPerAccepted => Accepted == 0 ? null : Usage.Total / Accepted;

    public long? TokensPerRun => RunCount == 0 ? null : Usage.Total / RunCount;

    public double? ToolErrorRate => ToolCalls == 0 ? null : (double)ToolErrors / ToolCalls;
}

/// <summary>ハーネスの版1つ＝ハーネスのパスを最後に変えたコミット（§37.6）。</summary>
public sealed record HarnessVersion(string Commit, DateTimeOffset At, string Subject)
{
    public string ShortCommit => Commit.Length > 8 ? Commit[..8] : Commit;
}

/// <summary>版の比較の1行。<see cref="Version"/> が null の行は「最初の記録より前」。</summary>
public sealed record HarnessVersionRow(HarnessVersion? Version, HarnessSummary Summary, int CandidateOccurrences);

public static class HarnessMetrics
{
    public static HarnessSummary Summarize(
        IEnumerable<HarnessRun> runs, IReadOnlyDictionary<string, HarnessVerdict> verdicts)
    {
        int count = 0, accepted = 0, rejected = 0, calls = 0, errors = 0, costRuns = 0;
        var usage = default(RunTokenUsage);
        decimal cost = 0;
        foreach (var run in runs)
        {
            count++;
            usage += run.Usage;
            calls += run.ToolCallCountExcludingRejected;
            errors += run.ToolErrorCount;
            if (run.CostUsd is { } c)
            {
                costRuns++;
                cost += c;
            }
            if (verdicts.TryGetValue(run.Id, out var v))
            {
                if (v.Verdict == HarnessVerdictKind.Accepted) accepted++;
                else rejected++;
            }
        }
        return new HarnessSummary(count, accepted, rejected, usage, calls, errors, costRuns, cost);
    }

    /// <summary>版ごとの比較（新しい版が先）。候補の発生数は、その版の Run に属する根拠の数。
    /// 版の引き方（<paramref name="versionOf"/>）は呼び出し側が決める——マルチルートでは Run の作業フォルダーごとに
    /// 履歴が違うので、1本の時系列では引けない。</summary>
    public static IReadOnlyList<HarnessVersionRow> CompareVersions(
        IReadOnlyList<HarnessRun> runs,
        IReadOnlyDictionary<string, HarnessVerdict> verdicts,
        Func<HarnessRun, HarnessVersion?> versionOf,
        IReadOnlyList<HarnessCandidate> candidates)
    {
        var occurrencesByRun = candidates
            .SelectMany(c => c.Evidence)
            .GroupBy(e => e.RunId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return runs
            .GroupBy(versionOf)
            .Select(g => new HarnessVersionRow(
                g.Key,
                Summarize(g, verdicts),
                g.Sum(r => occurrencesByRun.TryGetValue(r.Id, out var n) ? n : 0)))
            .OrderByDescending(row => row.Version?.At ?? DateTimeOffset.MinValue)
            .ToList();
    }
}

/// <summary>版の時系列。Run の開始時刻から、その時点で効いていた版を引く。</summary>
public sealed class HarnessVersionTimeline
{
    private readonly List<HarnessVersion> _versions;

    public HarnessVersionTimeline(IEnumerable<HarnessVersion> versions)
        => _versions = versions.OrderBy(v => v.At).ToList();

    public static HarnessVersionTimeline Empty { get; } = new([]);

    public IReadOnlyList<HarnessVersion> Versions => _versions;

    /// <summary><paramref name="at"/> 以前で最後の版。最初の版より前なら null。</summary>
    public HarnessVersion? VersionAt(DateTimeOffset at)
    {
        HarnessVersion? found = null;
        foreach (var v in _versions)
        {
            if (v.At > at)
                break;
            found = v;
        }
        return found;
    }

    /// <summary><c>git log --format=%H%x1f%ct%x1f%s</c> の出力を読む。</summary>
    public static HarnessVersionTimeline ParseGitLog(string output)
    {
        var versions = new List<HarnessVersion>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\x1f');
            if (parts.Length < 3 || !long.TryParse(parts[1], out var unix))
                continue;
            versions.Add(new HarnessVersion(parts[0], DateTimeOffset.FromUnixTimeSeconds(unix), parts[2]));
        }
        return new HarnessVersionTimeline(versions);
    }
}

/// <summary>
/// Run とタスクの対応（§37.3.1）。人間の入力が <c>.harness/tasks/&lt;id&gt;</c> へのパスか <c>task:&lt;id&gt;</c> を
/// 含むときだけ結び付ける——題の似ている・同じファイルを触った等の推測では結ばない。
/// </summary>
public static class HarnessTaskLinks
{
    public static IReadOnlyList<HarnessTask> For(HarnessRun run, IReadOnlyList<HarnessTask> tasks)
    {
        if (tasks.Count == 0 || run.Prompts.Count == 0)
            return [];
        var result = new List<HarnessTask>();
        foreach (var task in tasks)
        {
            var file = System.IO.Path.GetFileName(task.Path);
            foreach (var prompt in run.Prompts)
            {
                var normalized = prompt.Replace('\\', '/');
                // パスも印も語として現れるときだけ（tasks/fix が tasks/fix-login.md に当たらないように）。
                if (ContainsWord(normalized, $".harness/tasks/{file}")
                    || ContainsWord(normalized, $".harness/tasks/{task.Id}")
                    || ContainsWord(prompt, "task:" + task.Id))
                {
                    result.Add(task);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary><paramref name="mark"/> が語として現れるか（<c>task:fix</c> が <c>task:fix-login</c> に当たらないように）。</summary>
    private static bool ContainsWord(string prompt, string mark)
    {
        for (var at = prompt.IndexOf(mark, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = prompt.IndexOf(mark, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + mark.Length;
            var before = at == 0 || !IsIdChar(prompt[at - 1]);
            var after = end >= prompt.Length || !IsIdChar(prompt[end]);
            if (before && after)
                return true;
        }
        return false;
    }

    private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}
