using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// OpenAI Chat Completions 互換プロトコルの共通処理。
/// OpenAI / ローカルLLM / GitHub Copilot（認証後）が共用する。
/// </summary>
internal static class OpenAiProtocol
{
    /// <summary>会話とツール定義から Chat Completions リクエストボディを組み立てる。</summary>
    public static JsonObject BuildRequest(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        string model,
        int maxTokens,
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
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = messages
        };
        if (toolArray.Count > 0)
        {
            body["tools"] = toolArray;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    /// <summary>リクエストを送り、応答（テキスト / ツール呼び出し）をイベント化して流す。
    /// <paramref name="configure"/> で認証ヘッダ等を付与する。</summary>
    public static async IAsyncEnumerable<AgentEvent> SendAsync(
        HttpClient http,
        string endpoint,
        JsonObject body,
        string providerName,
        Action<HttpRequestMessage>? configure,
        [EnumeratorCancellation] CancellationToken ct)
    {
        JsonNode? root;
        AgentError? error = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(body)
            };
            configure?.Invoke(req);

            using var resp = await http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            root = resp.IsSuccessStatusCode ? JsonNode.Parse(json) : null;
            if (root is null) error = new AgentError($"{providerName} APIエラー {(int)resp.StatusCode}: {json}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            error = new AgentError($"{providerName} 呼び出し失敗: {ex.Message}");
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
}
