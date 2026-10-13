using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// 改善候補を、ハーネスのタスク（<c>.harness/tasks/improve-&lt;規則&gt;-&lt;日付&gt;.md</c>）として書き出す
/// （構想§5「改善そのものをハーネスのタスクにする」、§37.7）。観測事実と推測を節で分けて書く。
/// </summary>
public static class HarnessTaskWriter
{
    private const int MaxEvidenceLines = 10;

    /// <summary>候補の本文（Markdown）。クリップボードへのコピーも同じ本文。</summary>
    public static string ToMarkdown(HarnessCandidate candidate, DateTimeOffset now, Func<string, string> displayPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 改善：{candidate.Title}");
        sb.AppendLine();
        sb.AppendLine($"- 規則: `{candidate.RuleId}`");
        sb.AppendLine($"- 検出日: {now.ToLocalTime():yyyy-MM-dd}");
        sb.AppendLine(candidate.EstimatedTokens > 0
            ? $"- 頻度: {candidate.RunCount} Run ／ 推定 {FormatTokens(candidate.EstimatedTokens)} トークン（文字数÷{HarnessDetectors.CharsPerToken} の概算）"
            : $"- 頻度: {candidate.RunCount} Run");
        sb.AppendLine();
        sb.AppendLine("## 観測事実");
        sb.AppendLine();
        sb.AppendLine(candidate.Observation);
        sb.AppendLine();
        sb.AppendLine($"## 根拠（新しい順・最大 {MaxEvidenceLines} 件）");
        sb.AppendLine();
        foreach (var e in candidate.Evidence.Take(MaxEvidenceLines))
            sb.AppendLine(EvidenceLine(e, displayPath));
        if (candidate.Evidence.Count > MaxEvidenceLines)
            sb.AppendLine($"- ほか {candidate.Evidence.Count - MaxEvidenceLines} 件");
        sb.AppendLine();
        sb.AppendLine("## 改善の方向（推測）");
        sb.AppendLine();
        sb.AppendLine(candidate.Direction);
        sb.AppendLine();
        sb.AppendLine("## 受け入れ条件");
        sb.AppendLine();
        sb.AppendLine("- [ ] 変更後の版で、同じ規則の発生が減っている（ハーネスペイン「版の比較」）");
        sb.AppendLine("- [ ] 受け入れ1件あたりトークンとツール失敗率が悪化していない");
        return sb.ToString();
    }

    private const int BulkEvidenceLines = 5;

    /// <summary>
    /// 複数の候補をまとめて直させるプロンプト（§37.5.1）。Claude Code 等のエージェントに貼る前提で、
    /// 先頭に作業の指示、続けて候補ごとの観測事実・根拠・方向（推測）を並べる。直さない判断も許し、
    /// 最後に候補 ID ごとの対処を報告させる——人間が「対処済み」「見送り」を付け分けられるように。
    /// </summary>
    public static string ToBulkPrompt(IReadOnlyList<HarnessCandidate> candidates, DateTimeOffset now, Func<string, string> displayPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ハーネスの改善（一括）");
        sb.AppendLine();
        sb.AppendLine($"Loomo のハーネスペインが、Claude Code の作業記録から次の {candidates.Count} 件の改善候補を規則で検出した（{now.ToLocalTime():yyyy-MM-dd}）。");
        sb.AppendLine("これらを、エージェントの作業環境（ハーネス）を直すことで減らしてほしい。");
        sb.AppendLine();
        sb.AppendLine("## 進め方");
        sb.AppendLine();
        sb.AppendLine("- 直す対象はハーネスだけ：`CLAUDE.md` / `AGENTS.md`、`.claude/settings.json` の hooks、`.claude/skills/`・`.claude/agents/`・`.claude/commands/`、`.harness/`。プロダクトのコードは変えない。");
        sb.AppendLine("- 「観測事実」は記録から機械的に数えたもの。「改善の方向」「関連しそうな構成要素」は規則が出した推測なので、根拠（記録のパスと行）を必要なだけ確かめてから決める。");
        sb.AppendLine("- 同じ原因の候補はまとめて1か所で直す。指示文を足すときは短く、既存の節に寄せる（指示が膨らむと毎回の入力が増える）。");
        sb.AppendLine("- 偶然・誤検出・直すと別の害が出るものは直さなくてよい。その場合は理由を書く。");
        sb.AppendLine();
        sb.AppendLine("## 最後に報告すること");
        sb.AppendLine();
        sb.AppendLine("候補 ID ごとに1行で、次の表にして返す（人間がこれを見て対処済み／見送りを付ける）：");
        sb.AppendLine();
        sb.AppendLine("| 候補 ID | 対処（直した／見送り） | 変えたファイルと内容、または見送った理由 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine();
        sb.AppendLine("## 候補");
        foreach (var c in candidates)
        {
            sb.AppendLine();
            sb.AppendLine($"### `{c.Id}` {c.Title}");
            sb.AppendLine();
            sb.AppendLine(c.EstimatedTokens > 0
                ? $"- 頻度: {c.RunCount} Run ／ 推定 {FormatTokens(c.EstimatedTokens)} トークン"
                : $"- 頻度: {c.RunCount} Run");
            sb.AppendLine($"- 観測事実: {c.Observation}");
            sb.AppendLine($"- 改善の方向（推測）: {c.Direction}");
            sb.AppendLine("- 根拠（新しい順）:");
            foreach (var e in c.Evidence.Take(BulkEvidenceLines))
                sb.AppendLine("  " + EvidenceLine(e, displayPath));
            if (c.Evidence.Count > BulkEvidenceLines)
                sb.AppendLine($"  - ほか {c.Evidence.Count - BulkEvidenceLines} 件");
        }
        return sb.ToString();
    }

    private static string EvidenceLine(HarnessEvidence e, Func<string, string> displayPath)
    {
        var at = e.At is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "時刻不明";
        return $"- {at} 「{e.RunTitle}」 {e.Tool}: {e.Summary}（記録: `{displayPath(e.TranscriptPath)}:{e.LineNumber}`）";
    }

    /// <summary>書き出して、そのパスを返す。同名があれば <c>-2</c>, <c>-3</c>… を付ける（上書きしない）。</summary>
    public static string Write(string workspaceFolder, HarnessCandidate candidate, DateTimeOffset now, Func<string, string> displayPath)
    {
        var dir = Path.Combine(workspaceFolder, HarnessDefinitionReader.HarnessFolderName, "tasks");
        Directory.CreateDirectory(dir);
        var stem = $"improve-{candidate.RuleId}-{now.ToLocalTime():yyyyMMdd}";
        var path = Path.Combine(dir, stem + ".md");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{stem}-{n}.md");
        File.WriteAllText(path, ToMarkdown(candidate, now, displayPath));
        return path;
    }

    /// <summary>トークン数の短い表記（1.2M / 34.5k / 812）。</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };
}
