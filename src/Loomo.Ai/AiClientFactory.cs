using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using System.Net.Http;
using sk0ya.Loomo.Ai.Clients;

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

    public IAiClient Resolve(AiProvider provider) =>
        new OpenAiCompatibleClient(_httpFactory.CreateClient("ai"), _settings);
}
