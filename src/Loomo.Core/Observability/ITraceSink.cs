namespace sk0ya.Loomo.Core.Observability;

/// <summary>
/// AI操作トレースの記録先（設計書 §20.4）。<see cref="Agent.AgentOrchestrator"/> が
/// 既知のポイントで <see cref="Record"/> を呼ぶ。実装は fire-and-forget（呼び出し側を待たせない）。
/// <para>
/// 既定は <see cref="NullTraceSink"/>（no-op）。Core を汚さず、トレース無効時やテストは
/// そのまま動く。実 IO 実装は <see cref="JsonlTraceSink"/>。
/// </para>
/// </summary>
public interface ITraceSink
{
    /// <summary>
    /// 1イベントを記録する。連番(<c>seq</c>)・時刻(<c>ts</c>)は実装側が採番・付与する。
    /// 例外は内部で握りつぶし、ログ記録がエージェント動作を妨げないようにする。
    /// </summary>
    /// <param name="sessionId">所属セッションID（trace ファイル名）。</param>
    /// <param name="turnId">所属ターンID。セッション系イベントでは null。</param>
    /// <param name="kind"><see cref="TraceKinds"/> のいずれか。</param>
    /// <param name="payload">種別ごとの内容（匿名オブジェクト可）。</param>
    void Record(string sessionId, string? turnId, string kind, object? payload);
}
