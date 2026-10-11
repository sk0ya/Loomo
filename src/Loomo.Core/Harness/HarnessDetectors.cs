using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// 改善候補を<b>規則だけで</b>検出する（§37.5）。同じ記録からは同じ候補が出る——LLM は使わない。
/// <para>候補は「規則 × 対象」で束ね、何 Run で起きたか（頻度）と推定トークン（影響）を持たせる。
/// 推定トークンは文字数 ÷ <see cref="CharsPerToken"/> の概算で、画面では必ず「推定」と書く。</para>
/// </summary>
public static class HarnessDetectors
{
    public const int CharsPerToken = 4;
    /// <summary>これ以上の結果を「過大なツール出力」とする。</summary>
    public const int LargeOutputChars = 20_000;
    /// <summary>1 Run 内で同じファイルをこの回数以上読んだら「繰り返し読込」。</summary>
    public const int RereadThreshold = 3;
    /// <summary>1候補が抱える根拠の上限（メモリと一覧の長さを抑える）。</summary>
    public const int MaxEvidencePerCandidate = 200;

    public const string RuleReread = "reread";
    public const string RuleLargeOutput = "large-output";
    public const string RuleRetry = "retry-same-command";
    public const string RuleEditMismatch = "edit-mismatch";
    public const string RuleUnverified = "unverified-change";

    private static readonly Regex BuildOrTest = new(
        @"\b(dotnet\s+(build|test|msbuild)|msbuild(\.exe)?\b|(npm|pnpm|yarn)\s+(run\s+)?(test|build)|npx\s+(vitest|jest|tsc)|"
        + @"\bvitest\b|\bjest\b|\btsc\b|pytest|cargo\s+(build|test|check)|go\s+(build|test|vet)|gradlew?\s|mvn\s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal) { "Edit", "MultiEdit", "Write", "NotebookEdit" };
    private static readonly HashSet<string> ShellTools = new(StringComparer.Ordinal) { "Bash", "PowerShell" };
    private static readonly HashSet<string> NonSourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt", ".rst" };

    /// <summary>ビルド／テストのコマンドか（§37.5 の一覧）。</summary>
    public static bool IsBuildOrTest(string? command) => command is not null && BuildOrTest.IsMatch(command);

    public static IReadOnlyList<HarnessCandidate> Detect(IReadOnlyList<HarnessRun> runs)
    {
        var aggs = new Dictionary<string, Agg>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            DetectRereads(run, aggs);
            DetectLargeOutputs(run, aggs);
            DetectRetries(run, aggs);
            DetectEditMismatches(run, aggs);
            DetectUnverified(run, aggs);
        }
        return aggs.Values
            .Select(a => a.Build())
            .OrderByDescending(c => c.RunCount)
            .ThenByDescending(c => c.EstimatedTokens)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void DetectRereads(HarnessRun run, Dictionary<string, Agg> aggs)
    {
        // パス（＋読み始め位置）ごとの「最後に書き換えてから何回読んだか」と、2回目以降の読込。
        var counts = new Dictionary<string, (int Count, List<RunToolCall> Repeats)>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in run.ToolCalls)
        {
            if (call.FilePath is not { } path)
                continue;
            if (WriteTools.Contains(call.Tool))
            {
                foreach (var key in counts.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                    counts.Remove(key);
                continue;
            }
            if (call.Tool != "Read" || call.IsError || call.IsRejected)
                continue;
            var readKey = $"{path}|{call.ReadOffset ?? 0}";
            var (count, repeats) = counts.TryGetValue(readKey, out var v) ? v : (0, new List<RunToolCall>());
            count++;
            if (count >= 2)
                repeats.Add(call);
            counts[readKey] = (count, repeats);
            if (count == RereadThreshold)
            {
                foreach (var r in repeats)
                    Get(aggs, RuleReread, path.ToLowerInvariant(), () => RereadAgg(path)).Add(run, r, r.ResultChars);
            }
            else if (count > RereadThreshold)
            {
                Get(aggs, RuleReread, path.ToLowerInvariant(), () => RereadAgg(path)).Add(run, call, call.ResultChars);
            }
        }
    }

    private static Agg RereadAgg(string path) => new(
        RuleReread, path.ToLowerInvariant(), $"同じファイルの繰り返し読込：{Path.GetFileName(path)}",
        HarnessCandidateCategory.Tokens,
        $"1 Run の中で {path} を、書き換えを挟まずに {RereadThreshold} 回以上読んでいる。",
        "読んだ内容を引き継ぐ手順（調査結果を小さくまとめて持つ・必要な範囲だけ読む）を指示に書く。",
        [HarnessComponentKind.Instructions]);

    private static void DetectLargeOutputs(HarnessRun run, Dictionary<string, Agg> aggs)
    {
        foreach (var call in run.ToolCalls)
        {
            if (!call.HasResult || call.IsRejected || (call.ResultChars < LargeOutputChars && !call.OutputPersisted))
                continue;
            string key, label, direction;
            HarnessComponentKind[] related;
            if (call.Tool == "Read" && call.FilePath is { } path)
            {
                key = "read:" + path.ToLowerInvariant();
                label = Path.GetFileName(path);
                direction = "ファイル全体ではなく、検索で位置を絞ってから offset／limit で必要な範囲だけ読む。";
                related = [HarnessComponentKind.Instructions];
            }
            else if (ShellTools.Contains(call.Tool) && call.Command is { } command)
            {
                var head = CommandKey(command);
                key = "cmd:" + head;
                label = head;
                if (IsBuildOrTest(command))
                {
                    direction = "成功時は要約だけ、失敗時は失敗箇所だけを返す（出力の絞り込みを Hook かラッパーに持たせる）。";
                    related = [HarnessComponentKind.Hook, HarnessComponentKind.Instructions];
                }
                else
                {
                    direction = "出力を head／Select-Object・grep などで絞る指示を足す。";
                    related = [HarnessComponentKind.Instructions];
                }
            }
            else
            {
                key = "tool:" + call.Tool;
                label = call.Tool;
                direction = "ツールに渡す条件を絞り、返る量を減らす。";
                related = [HarnessComponentKind.Instructions];
            }
            Get(aggs, RuleLargeOutput, key, () => new Agg(
                RuleLargeOutput, key, $"過大なツール出力：{label}", HarnessCandidateCategory.Tokens,
                $"{call.Tool} の結果が {LargeOutputChars:N0} 字以上（または別ファイルへ逃がされるほど大きい）。",
                direction, related)).Add(run, call, call.ResultChars);
        }
    }

    private static void DetectRetries(HarnessRun run, Dictionary<string, Agg> aggs)
    {
        var failures = run.ToolCalls
            .Where(c => c.IsError && ShellTools.Contains(c.Tool) && c.Command is not null)
            .GroupBy(c => Normalize(c.Command!), StringComparer.Ordinal)
            .Where(g => g.Count() >= 2);
        foreach (var group in failures)
        {
            var head = CommandKey(group.First().Command!);
            var agg = Get(aggs, RuleRetry, head, () => new Agg(
                RuleRetry, head, $"同じコマンドの失敗反復：{head}", HarnessCandidateCategory.Rework,
                "1 Run の中で同じコマンドが2回以上失敗している。",
                IsBuildOrTest(group.First().Command)
                    ? "失敗の型（ビルドエラー・テスト失敗）ごとに、直す前に確かめることをルールにする。"
                    : "前提（パス・シェルの書き方・必要なツール）を先に確かめる手順をルールか指示に書く。",
                [HarnessComponentKind.Rule, HarnessComponentKind.Instructions]));
            foreach (var call in group)
                agg.Add(run, call, call.ResultChars);
        }
    }

    private static void DetectEditMismatches(HarnessRun run, Dictionary<string, Agg> aggs)
    {
        foreach (var call in run.ToolCalls)
        {
            if (!call.IsError || call.Tool is not ("Edit" or "MultiEdit") || call.ErrorHead is not { } error)
                continue;
            if (!(error.Contains("String to replace not found", StringComparison.OrdinalIgnoreCase)
                  || error.Contains("matches of the string", StringComparison.OrdinalIgnoreCase)
                  || error.Contains("has not been read", StringComparison.OrdinalIgnoreCase)
                  || error.Contains("modified since", StringComparison.OrdinalIgnoreCase)))
                continue;
            Get(aggs, RuleEditMismatch, "edit", () => new Agg(
                RuleEditMismatch, "edit", "編集の空振り", HarnessCandidateCategory.Rework,
                "Edit が置換対象を見つけられず（または読んでいない・読んだ後に変わった）失敗している。",
                "編集の直前に対象範囲を読み直し、置換元を写し取る手順を指示に書く。",
                [HarnessComponentKind.Instructions])).Add(run, call, call.InputChars);
        }
    }

    private static void DetectUnverified(HarnessRun run, Dictionary<string, Agg> aggs)
    {
        var lastEdit = -1;
        for (var i = 0; i < run.ToolCalls.Count; i++)
        {
            var call = run.ToolCalls[i];
            if (WriteTools.Contains(call.Tool) && !call.IsError && !call.IsRejected && call.FilePath is { } path
                && !NonSourceExtensions.Contains(Path.GetExtension(path)))
                lastEdit = i;
        }
        if (lastEdit < 0)
            return;
        for (var i = lastEdit + 1; i < run.ToolCalls.Count; i++)
        {
            if (ShellTools.Contains(run.ToolCalls[i].Tool) && IsBuildOrTest(run.ToolCalls[i].Command))
                return;
        }
        var edit = run.ToolCalls[lastEdit];
        Get(aggs, RuleUnverified, "unverified", () => new Agg(
            RuleUnverified, "unverified", "検証なしの変更", HarnessCandidateCategory.Quality,
            "ソースを変更したのに、最後の変更より後にビルド／テストのコマンドが実行されていない。",
            "完了前にビルド・テストを走らせる工程を Workflow か指示に入れる。",
            [HarnessComponentKind.Workflow, HarnessComponentKind.Instructions])).Add(run, edit, 0);
    }

    /// <summary>コマンドを束ねる鍵：先頭の2語（<c>cd x &amp;&amp;</c> の前置きは外す）。</summary>
    public static string CommandKey(string command)
    {
        var text = Normalize(command);
        // 「cd C:/x && dotnet build」の cd は場所合わせで、何をしたかではない。
        var chained = Regex.Match(text, @"^cd\s+\S+\s*(&&|;)\s*(?<rest>.+)$");
        if (chained.Success)
            text = chained.Groups["rest"].Value;
        // 先頭が引用符つきの実行ファイル（"C:\Program Files\…\git.exe" status）なら、閉じ引用符までを1語にする。
        if (text.Length > 1 && text[0] is '"' or '\'' && text.IndexOf(text[0], 1) is var close and > 0)
            text = Path.GetFileName(text[1..close]) + text[(close + 1)..];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";
        var first = Path.GetFileName(words[0].Trim('"', '\''));
        return words.Length > 1 && !words[1].StartsWith('-') && !words[1].Contains('/') && !words[1].Contains('\\')
            ? $"{first} {words[1]}"
            : first;
    }

    private static string Normalize(string command) => Regex.Replace(command, @"\s+", " ").Trim();

    private static Agg Get(Dictionary<string, Agg> aggs, string rule, string key, Func<Agg> create)
    {
        var id = $"{rule}:{key}";
        if (!aggs.TryGetValue(id, out var agg))
            aggs[id] = agg = create();
        return agg;
    }

    private sealed class Agg(
        string rule, string key, string title, HarnessCandidateCategory category,
        string observation, string direction, IReadOnlyList<HarnessComponentKind> related)
    {
        private readonly List<HarnessEvidence> _evidence = new();
        private readonly HashSet<string> _runs = new(StringComparer.Ordinal);
        private long _chars;

        public void Add(HarnessRun run, RunToolCall call, int chars)
        {
            _runs.Add(run.Id);
            _chars += chars;
            if (_evidence.Count < MaxEvidencePerCandidate)
                _evidence.Add(new HarnessEvidence(run.Id, run.Title, call.TranscriptPath, call.LineNumber, call.At,
                    call.Tool, call.Summary, call.FilePath, chars));
        }

        public HarnessCandidate Build() => new(
            rule, key, title, category, observation, direction,
            _chars / CharsPerToken, _runs.Count,
            _evidence.OrderByDescending(e => e.At ?? DateTimeOffset.MinValue).ToList(), related);
    }
}
