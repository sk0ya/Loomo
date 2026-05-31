namespace AgentStudio.Core.Models;

/// <summary>
/// AIクライアントのストリーム / エージェントループから流れるイベント。
/// UI（AIバー）はこれを購読して逐次描画する。
/// </summary>
public abstract record AgentEvent;

/// <summary>逐次テキスト（ストリーミング）。</summary>
public sealed record TextDelta(string Text) : AgentEvent;

/// <summary>アシスタントがツール呼び出しを要求した。</summary>
public sealed record ToolUseRequested(ToolUse ToolUse) : AgentEvent;

/// <summary>ツールの実行を開始した（UI表示用）。</summary>
public sealed record ToolExecutionStarted(ToolUse ToolUse) : AgentEvent;

/// <summary>ツールの実行が完了した。</summary>
public sealed record ToolExecutionCompleted(ToolUse ToolUse, ToolResultMessage Result) : AgentEvent;

/// <summary>承認待ちに入った。</summary>
public sealed record ApprovalRequested(string ToolName, string Summary) : AgentEvent;

/// <summary>1ターン（アシスタントの応答）が完了。</summary>
public sealed record TurnCompleted(string? FinalText) : AgentEvent;

/// <summary>エラー。</summary>
public sealed record AgentError(string Message) : AgentEvent;
