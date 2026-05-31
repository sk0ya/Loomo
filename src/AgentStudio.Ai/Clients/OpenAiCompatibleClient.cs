using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentStudio.Core.Abstractions;
using AgentStudio.Core.Models;
using AgentStudio.Core.Tools;

namespace AgentStudio.Ai.Clients;

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

        var body = BuildRequest(conversation, tools, cfg, _settings.SystemPrompt);

        JsonNode? root;
        AgentError? error = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = JsonContent.Create(body)
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.Add("Authorization", $"Bearer {cfg.ApiKey}");

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            root = resp.IsSuccessStatusCode ? JsonNode.Parse(json) : null;
            if (root is null) error = new AgentError($"{Provider} APIエラー {(int)resp.StatusCode}: {json}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            error = new AgentError($"{Provider} 呼び出し失敗: {ex.Message}");
            root = null;
        }

        if (error is not null) { yield return error; yield break; }

        var message = root?["choices"]?[0]?["message"];
        if (message is null) { yield return new TurnCompleted(null); yield break; }

        var text = message["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(text))
            yield return new TextDelta(text);

        var toolCalls = message["tool_calls"]?.AsArray();
        var hadTool = false;
        if (toolCalls is not null)
            foreach (var call in toolCalls)
            {
                hadTool = true;
                var id = call?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var fn = call?["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = fn?["arguments"]?.GetValue<string>() ?? "{}";
                yield return new ToolUseRequested(new ToolUse(id, name, args));
            }

        if (!hadTool)
            yield return new TurnCompleted(text);
    }

    private static JsonObject BuildRequest(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        ProviderConfig cfg,
        string systemPrompt)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };

        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" });
                    break;

                case ChatRole.Assistant:
                    var asMsg = new JsonObject { ["role"] = "assistant", ["content"] = m.Text ?? "" };
                    if (m.ToolUses.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var use in m.ToolUses)
                            calls.Add(new JsonObject
                            {
                                ["id"] = use.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = use.Name,
                                    ["arguments"] = string.IsNullOrWhiteSpace(use.ArgumentsJson) ? "{}" : use.ArgumentsJson
                                }
                            });
                        asMsg["tool_calls"] = calls;
                    }
                    messages.Add(asMsg);
                    break;

                case ChatRole.Tool:
                    foreach (var r in m.ToolResults)
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = r.ToolUseId,
                            ["content"] = r.Content
                        });
                    break;
            }
        }

        var toolArray = new JsonArray();
        foreach (var t in tools)
            toolArray.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone()
                }
            });

        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["max_tokens"] = cfg.MaxTokens,
            ["messages"] = messages
        };
        if (toolArray.Count > 0)
        {
            body["tools"] = toolArray;
            body["tool_choice"] = "auto";
        }
        return body;
    }
}
