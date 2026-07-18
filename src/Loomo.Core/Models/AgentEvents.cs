namespace sk0ya.Loomo.Core.Models;

/// <summary>
/// AIクライアントのストリーム / エージェントループから流れるイベント。
/// UI（AIバー）はこれを購読して逐次描画する。
/// </summary>
public abstract record AgentEvent;

/// <summary>逐次テキスト（ストリーミング）。確定した応答本文として履歴・トランスクリプトへ積まれる。</summary>
public sealed record TextDelta(string Text) : AgentEvent;

/// <summary>
/// モデルがいま生成している<b>生</b>の逐次出力（揮発性）。<see cref="OnnxGenAiClient"/> は本文を終端まで
/// バッファしてからツール呼び出しか通常テキストかを判定するため、確定前のトークンはこのイベントで先に流す。
/// 履歴にもトランスクリプトにも積まず、進捗状況のライブプレビュー専用（次の確定イベント＝<see cref="TextDelta"/>
/// ／<see cref="ToolUseRequested"/>／<see cref="ToolCallParseFailed"/> が出たら役目を終える）。
/// これにより、ツール呼び出しJSONが確定前に生のまま回答へ漏れることなく、生成中の様子だけをリアルタイム表示できる。
/// </summary>
public sealed record RawTextDelta(string Text) : AgentEvent;

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
/// ローカル推論エンジンが自己計測したトークン数と load / prefill / decode 時間を載せ、
/// オーケストレーターが <c>ai.usage</c> トレースに記録する。
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
