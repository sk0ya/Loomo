using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// OpenAI Chat Completions 互換クライアント。OpenAI 本体・ローカルLLM（Ollama等）・
/// OpenAI互換エンドポイントを共通実装でカバーする。BaseUrl と Provider で切替。
/// </summary>
public sealed class OpenAiCompatibleClient : IAiClient
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    public OpenAiCompatibleClient(HttpClient http, AiSettings settings, AiProvider provider)
    {
        _http = http;
        _settings = settings;
        Provider = provider;
    }

    public AiProvider Provider { get; }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _settings.ConfigFor(Provider);
        var baseUrl = (cfg.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        var requiresKey = Provider != AiProvider.Local;
        if (requiresKey && string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            yield return new AgentError($"{Provider} APIキーが未設定です。");
            yield break;
        }

        // ローカルLLM は未起動なら Ollama の起動を試みる（手動起動を不要にする）。
        if (Provider == AiProvider.Local)
            await OllamaLauncher.EnsureRunningAsync(_http, baseUrl, ct);

        var body = OpenAiProtocol.BuildRequest(conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt);

        await foreach (var ev in OpenAiProtocol.SendAsync(
            _http, $"{baseUrl}/chat/completions", body, Provider.ToString(),
            req =>
            {
                if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.ApiKey}");
            },
            ct))
        {
            yield return ev;
        }
    }
}
