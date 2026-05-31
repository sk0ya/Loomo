using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// GitHub Copilot プロバイダ。Copilot はデバイス認証＋専用トークン交換が必要なため、
/// v1では未実装スロットとして用意（後回し可。OAuthデバイスフロー実装後に有効化）。
/// 認証後は OpenAI互換エンドポイント経由で動かせる見込み。
/// </summary>
public sealed class CopilotAiClient : IAiClient
{
    public AiProvider Provider => AiProvider.Copilot;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new AgentError(
            "GitHub Copilot プロバイダは未実装です（デバイス認証フローの実装後に有効化予定）。" +
            "当面は Claude / OpenAI / Local をご利用ください。");
    }
}
