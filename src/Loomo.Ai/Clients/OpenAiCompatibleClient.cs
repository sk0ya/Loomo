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
/// Ollama の OpenAI Chat Completions 互換エンドポイントを使うローカルLLMクライアント。
/// </summary>
public sealed class OpenAiCompatibleClient : IAiClient
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    public OpenAiCompatibleClient(HttpClient http, AiSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public AiProvider Provider => AiProvider.Local;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _settings.Local;
        var defaultBase = OllamaLauncher.DefaultBaseUrl;
        var baseUrl = (string.IsNullOrWhiteSpace(cfg.BaseUrl) ? defaultBase : cfg.BaseUrl).TrimEnd('/');

        var endpoint = $"{baseUrl}/chat/completions";
        void Authorize(HttpRequestMessage req)
        {
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.ApiKey}");
        }

        // ローカルLLM は未起動なら Ollama の起動を試みる（手動起動を不要にする）。
        await OllamaLauncher.EnsureRunningAsync(_http, baseUrl, ct);

        // SSE ストリーミング。先頭イベントが「ツール非対応」エラーなら includeTools:false で再送する。
        // （ストリームは貯め込めないので最初の1件だけ覗いて判定する。）
        var body = OpenAiProtocol.BuildRequest(conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt);
        DisableLocalThinking(body);
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
            DisableLocalThinking(fallbackBody);
            await foreach (var ev in OpenAiProtocol.SendStreamingAsync(
                _http, endpoint, fallbackBody, Provider.ToString(), Authorize, ct, extractThinking: true))
                yield return ev;
        }
    }

    private static void DisableLocalThinking(System.Text.Json.Nodes.JsonObject body)
    {
        // Ollama's OpenAI-compatible endpoint accepts both forms for thinking models.
        body["reasoning_effort"] = "none";
        body["reasoning"] = new System.Text.Json.Nodes.JsonObject { ["effort"] = "none" };
    }

    private static bool IsOllamaToolsUnsupportedError(string message)
        => message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not support tool", StringComparison.OrdinalIgnoreCase);
}
