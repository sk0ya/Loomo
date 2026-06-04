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
    IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct,
        AgentProfile? profile = null);
}

/// <summary>現在の設定に応じて IAiClient を解決する。</summary>
public interface IAiClientFactory
{
    IAiClient Resolve(AiProvider provider);
    IAiClient ResolveCurrent();
}
