using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>ハーネスの構成要素の種類（§37.2）。<c>.harness/</c> の宣言と、Claude Code が読む構成要素の両方を並べる。</summary>
public enum HarnessComponentKind
{
    /// <summary>エージェントへの指示（<c>CLAUDE.md</c> / <c>AGENTS.md</c>）。</summary>
    Instructions,
    Skill,
    /// <summary>サブエージェント定義（<c>.claude/agents/</c>）または <c>.harness/agents/</c>。</summary>
    Agent,
    /// <summary>スラッシュコマンド（<c>.claude/commands/</c>）。</summary>
    Command,
    /// <summary><c>.claude/settings*.json</c> の hooks（イベント名ごとに1要素）。</summary>
    Hook,
    Workflow,
    Rule,
    Evaluation,
}

/// <summary>構成要素1つ。<see cref="Id"/> は種類つきで一意（例 <c>skill:review</c>）。</summary>
public sealed record HarnessComponent(string Id, HarnessComponentKind Kind, string Name, string Path);

/// <summary><c>.harness/tasks/</c> の1タスク。ID は拡張子を除いたファイル名（§37.3）。</summary>
public sealed record HarnessTask(string Id, string Title, string Path);

/// <summary>ワークスペースのハーネス定義（フォルダーごとに読んだものを束ねたもの）。</summary>
public sealed record HarnessDefinition(
    IReadOnlyList<string> HarnessFolders,
    string? HarnessId,
    string? DeclaredVersion,
    IReadOnlyList<HarnessComponent> Components,
    IReadOnlyList<HarnessTask> Tasks)
{
    public static HarnessDefinition Empty { get; } = new([], null, null, [], []);

    /// <summary><c>.harness/</c> がどこかのフォルダーにあるか。</summary>
    public bool HasHarnessFolder => HarnessFolders.Count > 0;
}

/// <summary>トークン使用量。値は記録に書かれた数（推定ではない）。</summary>
public readonly record struct RunTokenUsage(long Input, long Output, long CacheRead, long CacheCreation)
{
    public long Total => Input + Output + CacheRead + CacheCreation;

    /// <summary>入力側（input + cache_read + cache_creation）に占める cache_read の割合。入力が無ければ null。</summary>
    public double? CacheReadRatio
    {
        get
        {
            var prompt = Input + CacheRead + CacheCreation;
            return prompt == 0 ? null : (double)CacheRead / prompt;
        }
    }

    public static RunTokenUsage operator +(RunTokenUsage a, RunTokenUsage b)
        => new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheCreation + b.CacheCreation);
}

/// <summary>ツール呼び出し1回とその結果。</summary>
public sealed record RunToolCall
{
    /// <summary>Run 内の通し番号（本体→サブエージェントの順）。</summary>
    public required int Index { get; init; }
    /// <summary>記録ファイル（<see cref="TranscriptPath"/>）の中の1始まりの行番号（tool_use の行）。</summary>
    public required int LineNumber { get; init; }
    public required string TranscriptPath { get; init; }
    public DateTimeOffset? At { get; init; }
    public required string Tool { get; init; }
    /// <summary>一覧に出す短い説明（コマンドの1行目・ファイルパス・検索語）。</summary>
    public required string Summary { get; init; }
    /// <summary>Read/Edit/Write 等の対象ファイル（絶対パス）。</summary>
    public string? FilePath { get; init; }
    /// <summary>Read の <c>offset</c>（範囲を変えた読み直しを「同じ読込」と数えないため）。</summary>
    public long? ReadOffset { get; init; }
    /// <summary>Bash/PowerShell のコマンド全文。</summary>
    public string? Command { get; init; }
    /// <summary>入力（arguments）の JSON の文字数。</summary>
    public int InputChars { get; init; }
    public bool HasResult { get; init; }
    /// <summary>結果が <c>is_error</c>（人間の拒否は含まない）。</summary>
    public bool IsError { get; init; }
    /// <summary>人間がツールの実行を拒否した。失敗には数えない（§37.2.1）。</summary>
    public bool IsRejected { get; init; }
    /// <summary>結果の文字数。<see cref="OutputPersisted"/> のときは CLI が書いた元の大きさから換算した値。</summary>
    public int ResultChars { get; init; }
    /// <summary>結果が大きすぎて別ファイルへ逃がされた（<c>persisted-output</c>）。</summary>
    public bool OutputPersisted { get; init; }
    /// <summary>失敗時の結果の先頭（1行に畳んだもの）。</summary>
    public string? ErrorHead { get; init; }
    public bool FromSubagent { get; init; }
}

/// <summary>Run＝エージェントの1セッション（本体＋サブエージェント、§37.3.1）。</summary>
public sealed record HarnessRun
{
    public required string Id { get; init; }
    public required string TranscriptPath { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
    public RunTokenUsage Usage { get; init; }
    /// <summary>CLI が記録した料金（<c>cost-state</c>）。無ければ null＝不明（推計で埋めない）。</summary>
    public decimal? CostUsd { get; init; }
    public IReadOnlyList<RunToolCall> ToolCalls { get; init; } = [];
    /// <summary>人間の入力（本体のみ・メタ行は除く）。</summary>
    public IReadOnlyList<string> Prompts { get; init; } = [];
    /// <summary>呼ばれた Skill の名前（観測）。</summary>
    public IReadOnlyList<string> SkillsUsed { get; init; } = [];
    /// <summary>呼ばれたサブエージェントの種類（観測）。</summary>
    public IReadOnlyList<string> AgentsUsed { get; init; } = [];
    public int SubagentCount { get; init; }

    public TimeSpan? Duration => StartedAt is { } s && EndedAt is { } e && e >= s ? e - s : null;
    public int ToolErrorCount { get; init; }
    public int ToolCallCountExcludingRejected { get; init; }
}

/// <summary>人間の判定（§37.3 <c>verdicts.json</c>）。</summary>
public enum HarnessVerdictKind
{
    Accepted,
    Rejected,
}

public sealed record HarnessVerdict(HarnessVerdictKind Verdict, DateTimeOffset At, string? Note);

/// <summary>改善候補の根拠1件＝ある Run のある行で起きたこと。</summary>
public sealed record HarnessEvidence(
    string RunId,
    string RunTitle,
    string TranscriptPath,
    int LineNumber,
    DateTimeOffset? At,
    string Tool,
    string Summary,
    string? FilePath,
    int Chars);

/// <summary>改善候補の分類（頭の色分けと並べ替えのため）。</summary>
public enum HarnessCandidateCategory
{
    /// <summary>トークン効率。</summary>
    Tokens,
    /// <summary>手戻り・失敗。</summary>
    Rework,
    /// <summary>品質（検証の欠落など）。</summary>
    Quality,
}

/// <summary>
/// 規則が検出した改善候補（§37.5）。<see cref="Observation"/> は観測事実、<see cref="Direction"/> と
/// <see cref="RelatedKinds"/> は規則が出す<b>推測</b>。<see cref="EstimatedTokens"/> は文字数 ÷ 4 の概算。
/// </summary>
public sealed record HarnessCandidate(
    string RuleId,
    string Key,
    string Title,
    HarnessCandidateCategory Category,
    string Observation,
    string Direction,
    long EstimatedTokens,
    int RunCount,
    IReadOnlyList<HarnessEvidence> Evidence,
    IReadOnlyList<HarnessComponentKind> RelatedKinds)
{
    /// <summary>規則×対象で一意。</summary>
    public string Id => $"{RuleId}:{Key}";
}
