using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using System.Net.Http;

namespace sk0ya.Loomo.Ai;

/// <summary>設定に応じて IAiClient を解決するファクトリ。</summary>
public sealed class AiClientFactory : IAiClientFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AiSettings _settings;

    public AiClientFactory(IHttpClientFactory httpFactory, AiSettings settings)
    {
        _httpFactory = httpFactory;
        _settings = settings;
    }

    public IAiClient ResolveCurrent() => Resolve(_settings.Provider);

    public IAiClient Resolve(AiProvider provider) => provider switch
    {
        AiProvider.Claude => new ClaudeAiClient(_httpFactory.CreateClient("ai"), _settings),
        AiProvider.OpenAI => new OpenAiCompatibleClient(_httpFactory.CreateClient("ai"), _settings, AiProvider.OpenAI),
        AiProvider.Local => new OpenAiCompatibleClient(_httpFactory.CreateClient("ai"), _settings, AiProvider.Local),
        AiProvider.Copilot => new CopilotAiClient(),
        _ => new StubAiClient()
    };
}
