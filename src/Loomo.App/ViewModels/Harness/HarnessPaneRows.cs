using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ハーネスペインの表示用の短い書式（数字の出し方を1か所にまとめる）。</summary>
internal static class HarnessFormat
{
    public static string Tokens(long tokens) => HarnessTaskWriter.FormatTokens(tokens);

    public static string Percent(double? ratio) => ratio is { } r ? (r * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

    public static string Time(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } d)
            return "—";
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}時間{d.Minutes}分";
        return d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}分" : $"{(int)d.TotalSeconds}秒";
    }

    public static string Kind(HarnessComponentKind kind) => kind switch
    {
        HarnessComponentKind.Instructions => "指示",
        HarnessComponentKind.Skill => "Skill",
        HarnessComponentKind.Agent => "エージェント",
        HarnessComponentKind.Command => "コマンド",
        HarnessComponentKind.Hook => "Hook",
        HarnessComponentKind.Workflow => "ワークフロー",
        HarnessComponentKind.Rule => "ルール",
        HarnessComponentKind.Evaluation => "評価",
        _ => kind.ToString(),
    };

    public static string Category(HarnessCandidateCategory category) => category switch
    {
        HarnessCandidateCategory.Tokens => "トークン",
        HarnessCandidateCategory.Rework => "手戻り",
        HarnessCandidateCategory.Quality => "品質",
        _ => category.ToString(),
    };

    public static string Version(HarnessVersion? version)
        => version is null ? "最初の記録より前" : $"{version.ShortCommit} {version.Subject}";
}

/// <summary>改善候補タブの左列：構成要素（先頭は「すべて」）。</summary>
public sealed class HarnessComponentRow
{
    public HarnessComponentRow(HarnessComponent? component, string usage, int candidateCount)
    {
        Component = component;
        Usage = usage;
        CandidateCount = candidateCount;
    }

    /// <summary>null は「すべて」。</summary>
    public HarnessComponent? Component { get; }
    public string KindLabel => Component is null ? "" : HarnessFormat.Kind(Component.Kind);
    public string Name => Component?.Name ?? "すべて";
    /// <summary>観測された使用（「3回」「観測なし」「—」）。</summary>
    public string Usage { get; }
    public int CandidateCount { get; }
    public string CandidateCountLabel => CandidateCount == 0 ? "" : CandidateCount.ToString(CultureInfo.InvariantCulture);
    public string ToolTip => Component is null ? "すべての改善候補" : $"{Component.Path}\n使用（観測）: {Usage}";
}

public sealed class HarnessCandidateRow(HarnessCandidate candidate)
{
    public HarnessCandidate Candidate => candidate;
    public string Title => candidate.Title;
    public string CategoryLabel => HarnessFormat.Category(candidate.Category);
    public string RunCountLabel => $"{candidate.RunCount} Run";
    public string EstimateLabel => candidate.EstimatedTokens > 0 ? $"推定 {HarnessFormat.Tokens(candidate.EstimatedTokens)}" : "";
    public bool IsQuality => candidate.Category == HarnessCandidateCategory.Quality;
    public bool IsRework => candidate.Category == HarnessCandidateCategory.Rework;
}

public sealed class HarnessEvidenceRow(HarnessEvidence evidence, string? fileDisplay)
{
    public HarnessEvidence Evidence => evidence;
    public string Time => HarnessFormat.Time(evidence.At);
    public string Tool => evidence.Tool;
    public string Summary => fileDisplay ?? evidence.Summary;
    public string RunTitle => evidence.RunTitle;
    public string CharsLabel => evidence.Chars > 0 ? $"{evidence.Chars:N0} 字" : "";
    public bool HasFile => evidence.FilePath is { } p && File.Exists(p);
    public string ToolTip => $"{evidence.RunTitle}\n{evidence.Summary}\n記録: {Path.GetFileName(evidence.TranscriptPath)}:{evidence.LineNumber}";
}

public sealed class HarnessRunRow(HarnessRun run, HarnessVersion? version, HarnessVerdict? verdict, IReadOnlyList<HarnessTaskLink> links)
{
    private readonly IReadOnlyList<HarnessTask> tasks = links.Select(l => l.Task).ToList();

    public HarnessRun Run => run;
    public string Title => run.Title;
    public string Started => HarnessFormat.Time(run.StartedAt);
    public string Duration => HarnessFormat.Duration(run.Duration);
    public string TokensLabel => HarnessFormat.Tokens(run.Usage.Total);
    public string ErrorsLabel => run.ToolErrorCount == 0 ? "" : $"失敗 {run.ToolErrorCount}";
    public string VersionLabel => version?.ShortCommit ?? "—";
    public string VerdictLabel => verdict?.Verdict switch
    {
        HarnessVerdictKind.Accepted => "受け入れ",
        HarnessVerdictKind.Rejected => "差し戻し",
        _ => "",
    };
    public bool IsAccepted => verdict?.Verdict == HarnessVerdictKind.Accepted;
    public bool IsRejected => verdict?.Verdict == HarnessVerdictKind.Rejected;
    /// <summary>一覧に出すタスクの短い名前。タスクファイルにチケットがあればチケットの鍵を添える。</summary>
    public string TasksLabel => links.Count == 0 ? "" : string.Join(", ", links.Select(l => l.Task switch
    {
        // チケット型タスクは tickets.tsv の Status（定義の今の値）を併記する（§37.11）。作業の跡だけで結んだものは
        // 「跡」と添えて、指示に書かれたものと見分ける（§37.11.3）。
        { LocalTicket: { } local } t => WithNotes(t.Id, local.Status, l.Basis == HarnessTaskLinkBasis.Observed ? "跡" : null),
        { Ticket: { } ticket, IsTicketOnly: false } t => $"{t.Id}（{ticket.Key}）",
        var t => t.Id,
    }));

    private static string WithNotes(string id, params string?[] notes)
        => notes.OfType<string>().ToList() is { Count: > 0 } n ? $"{id}（{string.Join("・", n)}）" : id;

    /// <summary>この Run に結び付いたチケット（タスクファイル経由のものも含む）。</summary>
    public IReadOnlyList<HarnessTicketRef> Tickets { get; } = links.Select(l => l.Task.Ticket).OfType<HarnessTicketRef>()
        .DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasTicket => Tickets.Count > 0;

    public string Detail
    {
        get
        {
            var lines = new List<string>
            {
                $"題: {run.Title}",
                $"開始: {HarnessFormat.Time(run.StartedAt)}   所要: {HarnessFormat.Duration(run.Duration)}",
                $"版: {HarnessFormat.Version(version)}（未コミットの変更は区別できない）",
                $"モデル: {(run.Models.Count == 0 ? "—" : string.Join(", ", run.Models))}",
                $"トークン: 計 {HarnessFormat.Tokens(run.Usage.Total)}（入力 {HarnessFormat.Tokens(run.Usage.Input)} ・ 出力 {HarnessFormat.Tokens(run.Usage.Output)} ・ "
                + $"キャッシュ読込 {HarnessFormat.Tokens(run.Usage.CacheRead)} ・ キャッシュ書込 {HarnessFormat.Tokens(run.Usage.CacheCreation)}）",
                $"キャッシュ読込率: {HarnessFormat.Percent(run.Usage.CacheReadRatio)}",
                $"費用: {(run.CostUsd is { } c ? "$" + c.ToString("0.00", CultureInfo.InvariantCulture) + "（CLI の記録）" : "不明（記録なし）")}",
                $"ツール: {run.ToolCallCountExcludingRejected} 回 ・ 失敗 {run.ToolErrorCount} ・ サブエージェント {run.SubagentCount}",
            };
            if (run.SkillsUsed.Count > 0)
                lines.Add($"Skill: {string.Join(", ", run.SkillsUsed.Distinct())}");
            if (links.Any(l => l.Basis == HarnessTaskLinkBasis.Declared))
                lines.Add($"タスク（指示に記載）: {string.Join(", ", links.Where(l => l.Basis == HarnessTaskLinkBasis.Declared).Select(l => DescribeTask(l.Task)))}");
            foreach (var l in links.Where(l => l.Basis == HarnessTaskLinkBasis.Observed))
                lines.Add($"タスク（作業の跡・指示には無い）: {DescribeTask(l.Task)} ― {l.Evidence}");
            if (verdict is not null)
                lines.Add($"判定: {VerdictLabel}（{HarnessFormat.Time(verdict.At)}）");
            if (run.Prompts.Count > 0)
                lines.Add($"最初の指示: {ClaudeTranscriptParser.Shorten(run.Prompts[0], 300)}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>チケットだけ：「AB#1234（題）」／題が引けなければ鍵だけ。ファイル：「id（題・チケット）」。</summary>
    private static string DescribeTask(HarnessTask t) => t switch
    {
        { LocalTicket: { } local } => $"{t.Id}（{t.Title}・{DescribeLocal(local)}）",
        { IsTicketOnly: true } => t.Title == t.Id ? $"{t.Id}（チケット）" : $"{t.Id}（{t.Title}）",
        { Ticket: { } ticket } => $"{t.Id}（{t.Title}・{ticket.Key}）",
        _ => $"{t.Id}（{t.Title}）",
    };

    /// <summary>チケット型タスクの定義側の値（tickets.tsv の今の値と detail のチェックリスト。推定は混ぜない）。</summary>
    private static string DescribeLocal(HarnessLocalTicket local)
    {
        var parts = new List<string>
        {
            local.Origin switch
            {
                HarnessTicketOrigin.Meta => $"meta/{local.Group}",
                HarnessTicketOrigin.Archive => $"archive/…/{local.Group}",
                _ => $"tickets/{local.Group}",
            },
            $"状態 {local.Status ?? "不明"}（tickets.tsv の現在値）",
        };
        if (local.Type is { } type)
            parts.Add($"種類 {type}");
        parts.Add(local.AcceptanceTotal is { } total ? $"受け入れ条件 {local.AcceptanceDone}/{total}" : "受け入れ条件の節なし");
        if (local.DependIds.Count > 0)
            parts.Add($"依存 {string.Join(", ", local.DependIds)}");
        return string.Join("・", parts);
    }
}

public sealed class HarnessCallRow(RunToolCall call)
{
    public RunToolCall Call => call;
    public string Time => call.At is { } t ? t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";
    public string Tool => call.FromSubagent ? "↳ " + call.Tool : call.Tool;
    public string Summary => call.IsError && call.ErrorHead is { } e ? $"{call.Summary}  ⟶ {e}" : call.Summary;
    public string CharsLabel => call.ResultChars > 0 ? $"{call.ResultChars:N0} 字" : "";
    public bool IsError => call.IsError;
    public bool IsLarge => call.OutputPersisted || call.ResultChars >= HarnessDetectors.LargeOutputChars;
}

/// <summary>構成タブの1行（設定されているもの＋観測された使用）。</summary>
public sealed class HarnessConfigRow(HarnessComponent component, string usage, string displayPath)
{
    public HarnessComponent Component => component;
    public string KindLabel => HarnessFormat.Kind(component.Kind);
    public string Name => component.Name;
    public string Usage => usage;
    public string PathLabel => displayPath;
}

public sealed class HarnessVersionRowViewModel(HarnessVersionRow row)
{
    /// <summary>単発の良い結果で判断しないための目安（§37.6）。</summary>
    public const int FewRunsThreshold = 3;

    public string VersionLabel => HarnessFormat.Version(row.Version);
    public string DateLabel => row.Version is { } v ? v.At.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
    public string RunsLabel => row.Summary.RunCount.ToString(CultureInfo.InvariantCulture);
    public string VerdictsLabel => $"{row.Summary.Accepted} ／ {row.Summary.Rejected}";
    public string TokensPerAcceptedLabel => row.Summary.TokensPerAccepted is { } t ? HarnessFormat.Tokens(t) : "受け入れなし";
    public string TokensPerRunLabel => row.Summary.TokensPerRun is { } t ? HarnessFormat.Tokens(t) : "—";
    public string ErrorRateLabel => HarnessFormat.Percent(row.Summary.ToolErrorRate);
    public string CandidatesPerRunLabel => row.Summary.RunCount == 0
        ? "—"
        : ((double)row.CandidateOccurrences / row.Summary.RunCount).ToString("0.0", CultureInfo.InvariantCulture);
    public string Note => row.Summary.RunCount < FewRunsThreshold ? "試行が少ない" : "";
}

/// <summary>期間の選択肢。</summary>
public sealed record HarnessPeriodOption(int Days, string Label);

/// <summary>点検タブの1行（§37.10）。<see cref="Folder"/> は雛形を書き出す先のワークスペースフォルダー。</summary>
public sealed class HarnessCheckRow(HarnessCheckItem item, string folder, string? folderLabel)
{
    public HarnessCheckItem Item => item;
    public string Folder => folder;
    public string Title => folderLabel is null ? item.Title : $"{item.Title} — {folderLabel}";
    public string StatusLabel => item.Status switch
    {
        HarnessCheckStatus.Present => "✓ あり",
        HarnessCheckStatus.Incomplete => "△ 不足",
        _ => "✕ 無い",
    };
    public string LevelLabel => item.Level switch
    {
        HarnessCheckLevel.Required => "必須",
        HarnessCheckLevel.Recommended => "推奨",
        _ => "任意",
    };
    public string Finding => item.Finding;
    /// <summary>根拠があることを一覧で示す（なぜ今それが要るかを観測が裏づけている）。</summary>
    public string EvidenceMark => item.Evidence is not null && item.NeedsWork ? "根拠あり" : "";
    public bool IsPresent => item.Status == HarnessCheckStatus.Present;
    public bool IsMissing => item.Status == HarnessCheckStatus.Missing;
    public bool CanScaffold => item.Scaffold.Count > 0;
    public bool CanOpen => item.ExistingPath is not null;
    /// <summary>判定は雛形ではなく実行タブで Run に付けるもの。</summary>
    public bool IsVerdicts => item.Id == HarnessStructureCheck.ItemVerdicts;
}
