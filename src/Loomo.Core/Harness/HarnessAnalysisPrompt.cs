using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// 期間内の記録を Claude Code に読ませてハーネスの直し所を探させるプロンプト（§37.5）。
/// <para>直し所を規則で当てるのはやめた——規則が見られるのは表面の症状だけで、原因と直す先は結局推測だった。
/// ここでは<b>材料を渡すだけ</b>にする：どの記録を読めばよいか（パス）、どの Run から見るとよいか（判定・失敗・トークン）、
/// 何を直してよいか（ハーネスだけ）。見つけるのはエージェントで、直すかどうかは人間が決める。</para>
/// </summary>
public static class HarnessAnalysisPrompt
{
    /// <summary>一覧に並べる Run の上限（多すぎるとプロンプトそのものが入力を食う）。</summary>
    public const int MaxRuns = 60;

    public static string Build(
        IReadOnlyList<HarnessRun> runs,
        IReadOnlyDictionary<string, HarnessVerdict> verdicts,
        Func<HarnessRun, HarnessVersion?> versionOf,
        IReadOnlyList<HarnessComponent> components,
        DateTimeOffset since,
        DateTimeOffset now,
        Func<string, string> displayPath)
    {
        var sb = new StringBuilder();
        var summary = HarnessMetrics.Summarize(runs, verdicts);
        sb.AppendLine("# ハーネスの直し所を探す");
        sb.AppendLine();
        sb.AppendLine($"このワークスペースで動いた Claude Code の作業記録（{Date(since)}〜{Date(now)} の {runs.Count} Run）を読み、");
        sb.AppendLine("エージェントの作業環境（ハーネス）のどこを直せば、無駄なトークン・手戻り・失敗が減るかを探してほしい。");
        sb.AppendLine();
        sb.AppendLine("## 進め方");
        sb.AppendLine();
        sb.AppendLine("- 記録は JSONL で、1本が数 MB になる。全文を読まず、`rg` / `jq` / `Select-String` で必要な行（`tool_use`・`tool_result`・`is_error`・`usage`）だけ引く。サブエージェントの記録は `<記録と同名のフォルダー>/subagents/*.jsonl` にある。");
        sb.AppendLine("- 差し戻した Run・ツール失敗の多い Run・トークンの多い Run から見るとよい。");
        sb.AppendLine("- 見る観点の例：大きすぎるツール出力、同じファイルの読み直し、遠回りな探索、同じ失敗の繰り返し、編集の空振り、検証せずに終えた変更、指示や Skill が読まれていない／守られていない箇所。例に無い観点でもよい。");
        sb.AppendLine("- 1回きりの偶然は直し所にしない。何 Run で起きたかを数え、根拠（記録のパスと行番号）を挙げる。");
        sb.AppendLine("- 直す先はハーネスだけ：`CLAUDE.md` / `AGENTS.md`、`.claude/settings.json` の hooks、`.claude/skills/`・`.claude/agents/`・`.claude/commands/`、`.harness/`。プロダクトのコードは変えない。ユーザー全体（`~/.claude`）のものは他のワークスペースにも効くので、変えるなら先にそう断る。");
        sb.AppendLine("- 同じ原因はまとめて1か所で直す。指示文を足すなら短く、既存の節に寄せる（指示が膨らむと毎回の入力が増える）。");
        sb.AppendLine();
        sb.AppendLine("## 返してほしいもの");
        sb.AppendLine();
        sb.AppendLine("まず直し所を次の表にして示し、直すのは人間が了承したものだけにする。");
        sb.AppendLine();
        sb.AppendLine("| 直し所 | 観測（何 Run・根拠の記録:行） | 原因の見立て | 直す先と直し方 |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine();
        sb.AppendLine("## 期間の指標（Loomo が記録から数えたもの）");
        sb.AppendLine();
        sb.AppendLine($"- Run {summary.RunCount}・受け入れ {summary.Accepted}／差し戻し {summary.Rejected}（人間の判定。未判定は数えない）");
        sb.AppendLine($"- 総トークン {FormatTokens(summary.Usage.Total)}・キャッシュ読込率 {Percent(summary.Usage.CacheReadRatio)}・ツール失敗率 {Percent(summary.ToolErrorRate)}");

        if (components.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## いまのハーネスの構成要素");
            sb.AppendLine();
            // ユーザー全体の Skill・エージェント・コマンドは数が多く、場所も決まっている（~/.claude/<種類>/）ので種類ごとに1行へ畳む。
            // Hook は設定ファイルの中身なので、ファイルの行とは別に挙げない（イベント名だけ添える）。
            static bool Folded(HarnessComponent c)
                => c.UserScope && c.Kind is HarnessComponentKind.Skill or HarnessComponentKind.Agent or HarnessComponentKind.Command;
            foreach (var c in components.Where(c => !Folded(c) && c.Kind != HarnessComponentKind.Hook))
            {
                var hooks = components.Where(h => h.Kind == HarnessComponentKind.Hook
                        && string.Equals(h.Path, c.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Name.Split('（')[0]).ToList();
                sb.AppendLine($"- {KindLabel(c.Kind)}{(c.UserScope ? "（ユーザー全体）" : "")} `{displayPath(c.Path)}`"
                    + (hooks.Count > 0 ? $"（hooks: {string.Join(", ", hooks)}）" : ""));
            }
            foreach (var group in components.Where(Folded).GroupBy(c => c.Kind))
                sb.AppendLine($"- {KindLabel(group.Key)}（ユーザー全体）{group.Count()} 件: {string.Join(", ", group.Select(c => c.Name))}");
        }

        sb.AppendLine();
        sb.AppendLine("## Run（新しい順）");
        sb.AppendLine();
        sb.AppendLine("| 開始 | 題 | 版 | トークン | ツール／失敗 | 判定 | 記録 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var run in runs.Take(MaxRuns))
        {
            verdicts.TryGetValue(run.Id, out var verdict);
            var verdictLabel = verdict?.Verdict switch
            {
                HarnessVerdictKind.Accepted => "受け入れ",
                HarnessVerdictKind.Rejected => "差し戻し",
                _ => "",
            };
            var calls = $"{run.ToolCallCountExcludingRejected}／{run.ToolErrorCount}"
                + (run.SubagentCount > 0 ? $"（サブ {run.SubagentCount}）" : "");
            sb.AppendLine($"| {Time(run.StartedAt)} | {Cell(run.Title)} | {versionOf(run)?.ShortCommit ?? "—"} | "
                + $"{FormatTokens(run.Usage.Total)} | {calls} | {verdictLabel} | `{run.TranscriptPath}` |");
        }
        if (runs.Count > MaxRuns)
            sb.AppendLine($"\nほか {runs.Count - MaxRuns} Run（同じフォルダーにある古い記録）。");
        return sb.ToString();
    }

    /// <summary>トークン数の短い表記（3.08B / 1.2M / 34.5k / 812）。</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => (tokens / 1_000_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (tokens / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    private static string Percent(double? ratio)
        => ratio is { } r ? (r * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset? at)
        => at is { } t ? t.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary>表のセルに入れられる形（改行と <c>|</c> を潰し、長い題は切る）。</summary>
    private static string Cell(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Replace("|", "／").Trim();
        return flat.Length <= 60 ? flat : flat[..60] + "…";
    }

    private static string KindLabel(HarnessComponentKind kind) => kind switch
    {
        HarnessComponentKind.Instructions => "指示",
        HarnessComponentKind.Skill => "Skill",
        HarnessComponentKind.Agent => "エージェント",
        HarnessComponentKind.Command => "コマンド",
        HarnessComponentKind.Hook => "Hook",
        HarnessComponentKind.Workflow => "ワークフロー",
        HarnessComponentKind.Rule => "ルール",
        HarnessComponentKind.Evaluation => "評価",
        HarnessComponentKind.Settings => "設定",
        _ => kind.ToString(),
    };
}
