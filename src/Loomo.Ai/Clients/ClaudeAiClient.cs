using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// Anthropic Claude（Messages API / Tool Use）クライアント。
/// v1は非ストリーミングで応答を取得し、ブロック単位でイベント化する。
/// </summary>
public sealed class ClaudeAiClient : IAiClient
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    public ClaudeAiClient(HttpClient http, AiSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public AiProvider Provider => AiProvider.Claude;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _settings.Claude;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            yield return new AgentError("Claude APIキーが未設定です（設定 → Claude）。");
            yield break;
        }

        var body = BuildRequest(conversation, tools, cfg, _settings.SystemPrompt);

        JsonNode? root;
        AgentError? error = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Add("x-api-key", cfg.ApiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                error = new AgentError($"Claude APIエラー {(int)resp.StatusCode}: {json}");
                root = null;
            }
            else
            {
                root = JsonNode.Parse(json);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            error = new AgentError($"Claude 呼び出し失敗: {ex.Message}");
            root = null;
        }

        if (error is not null) { yield return error; yield break; }

        var content = root?["content"]?.AsArray();
        if (content is null) { yield return new TurnCompleted(null); yield break; }

        string? finalText = null;
        foreach (var block in content)
        {
            var type = block?["type"]?.GetValue<string>();
            if (type == "text")
            {
                var text = block!["text"]?.GetValue<string>() ?? "";
                finalText = (finalText ?? "") + text;
                yield return new TextDelta(text);
            }
            else if (type == "tool_use")
            {
                var id = block!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var name = block["name"]?.GetValue<string>() ?? "";
                var input = block["input"]?.ToJsonString() ?? "{}";
                yield return new ToolUseRequested(new ToolUse(id, name, input));
            }
        }

        // tool_use があった場合 Orchestrator が継続するので TurnCompleted は出さない
        var stop = root?["stop_reason"]?.GetValue<string>();
        if (stop != "tool_use")
            yield return new TurnCompleted(finalText);
    }

    private static JsonObject BuildRequest(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        ProviderConfig cfg,
        string systemPrompt)
    {
        var messages = new JsonArray();
        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Text ?? "" })
                    });
                    break;

                case ChatRole.Assistant:
                    var aContent = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Text))
                        aContent.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                    foreach (var use in m.ToolUses)
                        aContent.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = use.Id,
                            ["name"] = use.Name,
                            ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(use.ArgumentsJson) ? "{}" : use.ArgumentsJson)
                        });
                    if (aContent.Count > 0)
                        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = aContent });
                    break;

                case ChatRole.Tool:
                    var tContent = new JsonArray();
                    foreach (var r in m.ToolResults)
                        tContent.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = r.ToolUseId,
                            ["content"] = r.Content,
                            ["is_error"] = r.IsError
                        });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = tContent });
                    break;
            }
        }

        var toolArray = new JsonArray();
        foreach (var t in tools)
            toolArray.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone()
            });

        return new JsonObject
        {
            ["model"] = cfg.Model,
            ["max_tokens"] = cfg.MaxTokens,
            ["system"] = systemPrompt,
            ["messages"] = messages,
            ["tools"] = toolArray
        };
    }
}
