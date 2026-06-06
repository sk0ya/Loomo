namespace sk0ya.Loomo.Core.Models;

/// <summary>
/// AIクライアントのストリーム / エージェントループから流れるイベント。
/// UI（AIバー）はこれを購読して逐次描画する。
/// </summary>
public abstract record AgentEvent;

/// <summary>逐次テキスト（ストリーミング）。</summary>
public sealed record TextDelta(string Text) : AgentEvent;

/// <summary>モデルの思考（reasoning）テキスト。応答本文ではないので履歴には積まず、UI表示のみに使う。
/// ローカル推論モデル（&lt;think&gt; タグ / reasoning_content）から取り出す。</summary>
public sealed record ThinkingDelta(string Text) : AgentEvent;

/// <summary>アシスタントがツール呼び出しを要求した。</summary>
public sealed record ToolUseRequested(ToolUse ToolUse) : AgentEvent;

/// <summary>ツールの実行を開始した（UI表示用）。</summary>
public sealed record ToolExecutionStarted(ToolUse ToolUse) : AgentEvent;

/// <summary>ツールの実行が完了した。</summary>
public sealed record ToolExecutionCompleted(ToolUse ToolUse, ToolResultMessage Result) : AgentEvent;

/// <summary>承認待ちに入った。</summary>
public sealed record ApprovalRequested(string ToolName, string Summary) : AgentEvent;

/// <summary>
/// 1回のAI呼び出しの利用統計（トークン数・段階別の所要時間）。
/// Ollama は最終 <c>done</c> 行で <c>prompt_eval_count</c> / <c>eval_count</c>（トークン）と
/// <c>load_duration</c> / <c>prompt_eval_duration</c> / <c>eval_duration</c>（ナノ秒）を返す。
/// これを ms に直して載せ、オーケストレーターが <c>ai.usage</c> トレースに記録する。
/// 「重みロード / prefill / decode のどこが遅いか」を数値で切り分けるための内部イベント（UIには出さない）。
/// </summary>
public sealed record AiUsageReported(
    long? InputTokens,
    long? OutputTokens,
    double? LoadMs,
    double? PromptEvalMs,
    double? EvalMs,
    double? TotalMs) : AgentEvent;

/// <summary>
/// ツール呼び出しらしき本文を生成したが、JSON として解釈できなかった（不正なJSON）。
/// <see cref="RawText"/> はモデルが実際に出力した生テキスト。終端エラーにせず、これを履歴へ戻して
/// オーケストレータが AI に正しいJSONで出し直させる（＝AIが自己修正できる）／UI が生出力を可視化するための信号。
/// </summary>
public sealed record ToolCallParseFailed(string RawText) : AgentEvent;

/// <summary>1ターン（アシスタントの応答）が完了。</summary>
public sealed record TurnCompleted(string? FinalText) : AgentEvent;

/// <summary>エラー。</summary>
public sealed record AgentError(string Message) : AgentEvent;
