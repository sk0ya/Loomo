using System.Collections.Generic;
using System.Threading;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>
/// AIプロバイダ抽象。Claude / OpenAI / Copilot / ローカルLLM を同一IFで扱う。
/// </summary>
public interface IAiClient
{
    AiProvider Provider { get; }

    /// <summary>会話とツール定義を渡し、応答イベントをストリームで返す。</summary>
    IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}

/// <summary>現在の設定に応じて IAiClient を解決する。</summary>
public interface IAiClientFactory
{
    IAiClient Resolve(AiProvider provider);
    IAiClient ResolveCurrent();
}
