using System.Collections.Generic;
using System.Threading;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>
/// ローカルLLMクライアント抽象。
/// </summary>
public interface IAiClient
{
    AiProvider Provider { get; }

    /// <summary>会話とツール定義を渡し、応答イベントをストリームで返す。</summary>
    /// <param name="retryDiversify">直前ターンのツール呼び出しJSONが解釈不能で、これがその再試行のとき true。
    /// greedy デコードは同じ入力から同じ不正出力を再生産する（決定的崩壊）ため、再試行時だけ微小な
    /// サンプリング温度を入れて出力をずらし、決定的ループから脱出させる。</param>
    IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct,
        AgentProfile? profile = null,
        bool retryDiversify = false);
}

/// <summary>現在の設定に応じて IAiClient を解決する。</summary>
public interface IAiClientFactory
{
    IAiClient ResolveCurrent();
}
