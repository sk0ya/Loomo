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

/// <summary>Run をタスクに結んだ根拠。</summary>
public enum HarnessTaskLinkBasis
{
    /// <summary>人間の指示（スラッシュコマンドの引数を含む）に書かれていた。</summary>
    Declared,
    /// <summary>指示には無いが、エージェントがそのチケットを書き換えた（観測した作業の跡、§37.11.3）。</summary>
    Observed,
}

/// <summary>Run とタスクの結び付き1件。<see cref="Evidence"/> は作業の跡で結んだときの根拠の要約。</summary>
public sealed record HarnessTaskLink(HarnessTask Task, HarnessTaskLinkBasis Basis, string? Evidence = null);

/// <summary>
/// Run とタスクの対応（§37.3.1・§37.3.2）。人間の入力が <c>.harness/tasks/&lt;id&gt;</c> へのパスか <c>task:&lt;id&gt;</c>、
/// またはチケット（<see cref="HarnessTickets"/>）を含むときだけ結び付ける——題の似ている・同じファイルを触った等の
/// 推測では結ばない。チケットは <c>ticket:</c> でそれを宣言したタスクファイルがあればそのタスクに、無ければ
/// チケットそのものをタスクとして結ぶ（タスクファイルを書かずにチケットで仕事を回していても Run が束ねられる）。
/// チケット型タスク（<c>tickets/&lt;群&gt;/&lt;ID&gt;/</c>、§37.11）は、指示かスラッシュコマンドの引数に書かれた ID・パスが
/// 定義にある ID と一致したとき（指示）か、エージェントがそのチケットを実際に書き換えたとき（作業の跡、
/// <see cref="HarnessLocalTickets.FindTraces"/>）に結ぶ。どちらで結んだかは <see cref="HarnessTaskLink.Basis"/> で分ける。
/// </summary>
public static class HarnessTaskLinks
{
    /// <summary>
    /// Run とタスクの結び付き（根拠つき）。指示による結び付きが先、作業の跡だけで結んだものが後。
    /// 指示にも書かれ跡もあるタスクは指示の側に入れる（跡で上書きしない）。
    /// </summary>
    public static IReadOnlyList<HarnessTaskLink> Links(HarnessRun run, IReadOnlyList<HarnessTask> tasks)
    {
        var result = Declared(run, tasks).Select(t => new HarnessTaskLink(t, HarnessTaskLinkBasis.Declared)).ToList();
        var traces = HarnessLocalTickets.FindTraces(run);
        if (traces.Count == 0)
            return result;
        foreach (var task in tasks)
        {
            if (task.LocalTicket is null || result.Any(l => l.Task == task))
                continue;
            var mine = traces.Where(t => HarnessLocalTickets.Matches(task, t.Ticket)).ToList();
            if (mine.Count > 0)
                result.Add(new HarnessTaskLink(task, HarnessTaskLinkBasis.Observed, DescribeTraces(mine)));
        }
        return result;
    }

    /// <summary>結び付いたタスク（根拠を問わない）。</summary>
    public static IReadOnlyList<HarnessTask> For(HarnessRun run, IReadOnlyList<HarnessTask> tasks)
        => Links(run, tasks).Select(l => l.Task).ToList();

    /// <summary>「VCI-001-detail.md ほか1件を編集 3回・状態 working→done」。</summary>
    private static string DescribeTraces(IReadOnlyList<HarnessTicketTrace> traces)
    {
        var parts = new List<string>();
        var edits = traces.Where(t => t.Kind == HarnessTicketTraceKind.Edited).ToList();
        if (edits.Count > 0)
        {
            var files = edits.Select(t => t.Detail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            parts.Add($"{files[0]}{(files.Count > 1 ? $" ほか{files.Count - 1}件" : "")}を編集 {edits.Count}回");
        }
        parts.AddRange(traces.Where(t => t.Kind == HarnessTicketTraceKind.StatusChanged).Select(t => "状態 " + t.Detail));
        return string.Join("・", parts);
    }

    /// <summary>
    /// 結び付けに使う人間の文：指示と、打たれたスラッシュコマンド（<c>/ticket-run VCI-001</c>）。
    /// コマンドの引数も人間が書いたものなので、指示と同じ規則で読む。
    /// </summary>
    public static IEnumerable<string> TextsOf(HarnessRun run)
        => run.Prompts.Concat(run.Commands.Select(c => c.ToString()));

    /// <summary>人間が書いたもの（指示・スラッシュコマンドの引数）だけで結んだタスク。</summary>
    private static List<HarnessTask> Declared(HarnessRun run, IReadOnlyList<HarnessTask> tasks)
    {
        var texts = TextsOf(run).ToList();
        if (texts.Count == 0)
            return [];
        var result = new List<HarnessTask>();
        foreach (var task in tasks)
        {
            if (task.Path is null || task.LocalTicket is not null)
                continue;
            var file = System.IO.Path.GetFileName(task.Path);
            foreach (var prompt in texts)
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

        // チケット型タスク（§37.11）：定義にある ID と一致した言及だけ。一致しない ID の形の語（ABC-123）は拾わない。
        // 複数の ID が書かれていればすべてに、同じ ID が複数の群にあれば（群がパスで書かれていない限り）すべてに結ぶ。
        foreach (var mention in texts.SelectMany(HarnessLocalTickets.FindMentions).Distinct())
            result.AddRange(tasks.Where(t => HarnessLocalTickets.Matches(t, mention) && !result.Contains(t)));

        foreach (var ticket in texts.SelectMany(HarnessTickets.Find))
        {
            // 同じチケットを宣言したファイルが複数あれば（画面側と API 側で分けた等）すべてに結ぶ。
            var declared = tasks.Where(t => t.Path is not null && HarnessTickets.SameKey(t.Ticket?.Key, ticket.Key)).ToList();
            if (declared.Count > 0)
            {
                result.AddRange(declared.Where(t => !result.Contains(t)));
            }
            else if (!result.Any(t => HarnessTickets.SameKey(t.Ticket?.Key, ticket.Key)))
            {
                result.Add(HarnessTask.FromTicket(ticket));
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
