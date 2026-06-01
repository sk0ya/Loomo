using System;

namespace sk0ya.Loomo.Core.Observability;

/// <summary>
/// トレース1イベント（封筒）。JSONL の1行に対応する（設計書 §20.3）。
/// <para>
/// セッションJSON（再開用スナップショット）とは別ライフサイクルの「追記専用ログ」を構成する。
/// AIとのやり取り（プロンプト・ツール呼び出し・結果・タイミング・安全/承認結果）を順序付きで残す。
/// </para>
/// </summary>
public sealed record TraceEvent
{
    /// <summary>セッション内の通し番号（0 始まり・単調増加）。順序保証に使う。</summary>
    public long Seq { get; init; }

    /// <summary>記録時刻。</summary>
    public DateTimeOffset Ts { get; init; }

    /// <summary>所属セッションID（trace ファイル名と一致）。</summary>
    public string SessionId { get; init; } = "";

    /// <summary>所属ターンID。セッション系イベントでは null。</summary>
    public string? TurnId { get; init; }

    /// <summary>発生元エージェント。単一エージェント期は "root" 固定（§19.2 で複数化）。</summary>
    public string AgentId { get; init; } = "root";

    /// <summary>イベント種別（<see cref="TraceKinds"/>）。</summary>
    public string Kind { get; init; } = "";

    /// <summary>種別ごとの内容。匿名オブジェクト等を JSON シリアライズして格納する。</summary>
    public object? Payload { get; init; }
}

/// <summary>トレースイベントの種別文字列（設計書 §20.3 の kind 列）。</summary>
public static class TraceKinds
{
    public const string SessionStarted = "session.started";
    public const string TurnStarted = "turn.started";
    public const string AiMessage = "ai.message";
    public const string AiToolUse = "ai.tool_use";
    public const string SafetyEvaluated = "safety.evaluated";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalResolved = "approval.resolved";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string AiUsage = "ai.usage";
    public const string TurnCompleted = "turn.completed";
    public const string Error = "error";
}
