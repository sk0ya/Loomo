using System;
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
        {
            var at = e.At is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "時刻不明";
            sb.AppendLine($"- {at} 「{e.RunTitle}」 {e.Tool}: {e.Summary}（記録: `{displayPath(e.TranscriptPath)}:{e.LineNumber}`）");
        }
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
