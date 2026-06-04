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
/// Ollama ネイティブ API（<c>/api/chat</c>）を使うローカルLLMクライアント。
/// thinking モデルは <c>think</c>（真偽値）で確実に制御でき、思考は本文と分離して返る。
/// </summary>
public sealed class OllamaClient : IAiClient
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    public OllamaClient(HttpClient http, AiSettings settings)
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
        var host = OllamaLauncher.ResolveHost(cfg.BaseUrl);
        var endpoint = $"{host}/api/chat";
        void Authorize(HttpRequestMessage req)
        {
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.ApiKey}");
        }

        // ローカルLLM は未起動なら Ollama の起動を試みる（手動起動を不要にする）。
        await OllamaLauncher.EnsureRunningAsync(_http, host, ct);

        // 先頭イベントが「ツール非対応」エラーなら includeTools:false で再送する。
        // （ストリームは貯め込めないので最初の1件だけ覗いて判定する。）
        var body = OllamaProtocol.BuildRequest(conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt);
        ApplyThinking(body, cfg.ThinkingEffort);
        var fellBack = false;

        await using (var en = OllamaProtocol.SendChatAsync(
                _http, endpoint, body, Provider.ToString(), Authorize, ct)
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
            var fallbackBody = OllamaProtocol.BuildRequest(
                conversation, tools, cfg.Model, cfg.MaxTokens, _settings.SystemPrompt, includeTools: false);
            ApplyThinking(fallbackBody, cfg.ThinkingEffort);
            await foreach (var ev in OllamaProtocol.SendChatAsync(
                _http, endpoint, fallbackBody, Provider.ToString(), Authorize, ct))
                yield return ev;
        }
    }

    /// <summary>thinking の有効・無効を <c>think</c> に反映する。none で完全に無効化、それ以外は有効化。</summary>
    private static void ApplyThinking(System.Text.Json.Nodes.JsonObject body, string? effort)
    {
        var v = effort?.Trim().ToLowerInvariant();
        // ネイティブ API の think は真偽値（qwen3 等）。none のときだけ false で確実に thinking を止める。
        body["think"] = v is "low" or "medium" or "high";
    }

    private static bool IsOllamaToolsUnsupportedError(string message)
        => message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not support tool", StringComparison.OrdinalIgnoreCase);
}
