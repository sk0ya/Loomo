using System;
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
        // BaseUrl 未設定時の既定はプロバイダ依存（ローカルLLM は Ollama、それ以外は OpenAI）。
        var defaultBase = Provider == AiProvider.Local ? OllamaLauncher.DefaultBaseUrl : "https://api.openai.com/v1";
        var baseUrl = (string.IsNullOrWhiteSpace(cfg.BaseUrl) ? defaultBase : cfg.BaseUrl).TrimEnd('/');
        var requiresKey = Provider != AiProvider.Local;
        if (requiresKey && string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            yield return new AgentError($"{Provider} APIキーが未設定です。");
            yield break;
        }

        var endpoint = $"{baseUrl}/chat/completions";
        void Authorize(HttpRequestMessage req)
        {
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.ApiKey}");
        }

        // 非ローカルは従来どおり非ストリーミング（SSE 対応はローカルLLM 限定）。
        if (Provider != AiProvider.Local)
        {
            var body0 = OpenAiProtocol.BuildRequest(conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt);
            await foreach (var ev in OpenAiProtocol.SendAsync(
                _http, endpoint, body0, Provider.ToString(), Authorize, ct))
                yield return ev;
            yield break;
        }

        // ローカルLLM は未起動なら Ollama の起動を試みる（手動起動を不要にする）。
        await OllamaLauncher.EnsureRunningAsync(_http, baseUrl, ct);

        // SSE ストリーミング。先頭イベントが「ツール非対応」エラーなら includeTools:false で再送する。
        // （ストリームは貯め込めないので最初の1件だけ覗いて判定する。）
        var body = OpenAiProtocol.BuildRequest(conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt);
        var fellBack = false;

        await using (var en = OpenAiProtocol.SendStreamingAsync(
                _http, endpoint, body, Provider.ToString(), Authorize, ct, extractThinking: true)
            .GetAsyncEnumerator(ct))
        {
            if (await en.MoveNextAsync())
            {
                if (en.Current is AgentError err && IsOllamaToolsUnsupportedError(err.Message))
                {
                    fellBack = true;
                }
                else
                {
                    yield return en.Current;
                    while (await en.MoveNextAsync())
                        yield return en.Current;
                }
            }
        }

        if (fellBack)
        {
            var fallbackBody = OpenAiProtocol.BuildRequest(
                conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt, includeTools: false);
            await foreach (var ev in OpenAiProtocol.SendStreamingAsync(
                _http, endpoint, fallbackBody, Provider.ToString(), Authorize, ct, extractThinking: true))
                yield return ev;
        }
    }

    private static bool IsOllamaToolsUnsupportedError(string message)
        => message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not support tool", StringComparison.OrdinalIgnoreCase);
}
