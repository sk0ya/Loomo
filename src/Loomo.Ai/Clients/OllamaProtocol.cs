using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using sk0ya.Loomo.Ai.Http;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// Ollama ネイティブ API（<c>POST /api/chat</c>）のプロトコル処理。
/// OpenAI互換エンドポイントと違い、思考は <c>message.thinking</c> として分離して返り、
/// thinking の有効・無効は <c>think</c>（真偽値）で確実に制御できる。
/// </summary>
internal static class OllamaProtocol
{
    /// <summary>会話とツール定義から /api/chat リクエストボディを組み立てる。</summary>
    public static JsonObject BuildRequest(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        string model,
        int maxTokens,
        string systemPrompt,
        bool includeTools = true)
    {
        if (!includeTools)
        {
            systemPrompt +=
                "\n\n現在選択中のモデルはツール呼び出しに対応していません。" +
                "ファイル操作やコマンド実行はできないため、必要な操作はユーザーに具体的に案内してください。";
        }

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };

        // ツール結果メッセージに tool_name を添えるため、tool_use の id→name を覚えておく。
        var toolNameById = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" });
                    break;

                case ChatRole.Assistant:
                    var assistantText = m.Text ?? "";
                    if (!includeTools && m.ToolUses.Count > 0)
                        assistantText = AppendToolUseSummary(assistantText, m.ToolUses);

                    var asMsg = new JsonObject { ["role"] = "assistant", ["content"] = assistantText };
                    if (includeTools && m.ToolUses.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var use in m.ToolUses)
                        {
                            toolNameById[use.Id] = use.Name;
                            calls.Add(new JsonObject
                            {
                                ["function"] = new JsonObject
                                {
                                    ["name"] = use.Name,
                                    // ネイティブ API は arguments を文字列ではなくオブジェクトで受け取る。
                                    ["arguments"] = ParseArguments(use.ArgumentsJson)
                                }
                            });
                        }
                        asMsg["tool_calls"] = calls;
                    }
                    messages.Add(asMsg);
                    break;

                case ChatRole.Tool:
                    if (includeTools)
                    {
                        foreach (var r in m.ToolResults)
                        {
                            var toolMsg = new JsonObject
                            {
                                ["role"] = "tool",
                                ["content"] = r.Content
                            };
                            if (toolNameById.TryGetValue(r.ToolUseId, out var name))
                                toolMsg["tool_name"] = name;
                            messages.Add(toolMsg);
                        }
                    }
                    else
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = BuildToolResultSummary(m.ToolResults)
                        });
                    }
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["options"] = new JsonObject { ["num_predict"] = maxTokens }
        };

        if (includeTools && tools.Count > 0)
        {
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
            body["tools"] = toolArray;
        }

        return body;
    }

    private static JsonNode ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(argumentsJson) ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string AppendToolUseSummary(string text, IReadOnlyList<ToolUse> uses)
    {
        var prefix = string.IsNullOrWhiteSpace(text) ? "" : text + "\n\n";
        var lines = new List<string> { "過去に要求されたツール呼び出し:" };
        foreach (var use in uses)
            lines.Add($"- {use.Name}: {use.ArgumentsJson}");
        return prefix + string.Join("\n", lines);
    }

    private static string BuildToolResultSummary(IReadOnlyList<ToolResultMessage> results)
    {
        var lines = new List<string> { "過去のツール実行結果:" };
        foreach (var result in results)
        {
            var status = result.IsError ? "error" : "ok";
            lines.Add($"- {result.ToolUseId} ({status}): {result.Content}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// <c>/api/chat</c> を stream:true で叩き、改行区切りJSON（NDJSON）を逐次イベント化する。
    /// 思考は <c>message.thinking</c>、本文は <c>message.content</c>、ツール呼び出しは
    /// <c>message.tool_calls</c>（arguments はオブジェクト）から取り出す。
    /// </summary>
    public static async IAsyncEnumerable<AgentEvent> SendChatAsync(
        HttpClient http,
        string endpoint,
        JsonObject body,
        string providerName,
        Action<HttpRequestMessage>? configure,
        [EnumeratorCancellation] CancellationToken ct)
    {
        body["stream"] = true;

        HttpResponseMessage? resp = null;
        AgentError? error = null;
        try
        {
            resp = await HttpRetry.SendAsync(http, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
                configure?.Invoke(req);
                return req;
            }, ct, completionOption: HttpCompletionOption.ResponseHeadersRead);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                error = new AgentError($"{providerName} APIエラー {(int)resp.StatusCode}: {ExtractError(errBody)}");
            }
        }
        catch (OperationCanceledException) { resp?.Dispose(); throw; }
        catch (Exception ex) { error = new AgentError($"{providerName} 呼び出し失敗: {ex.Message}"); }

        if (error is not null) { resp?.Dispose(); yield return error; yield break; }

        Stream? netStream = null;
        StreamReader? reader = null;
        try
        {
            netStream = await resp!.Content.ReadAsStreamAsync(ct);
            reader = new StreamReader(netStream);
        }
        catch (OperationCanceledException) { resp!.Dispose(); throw; }
        catch (Exception ex) { error = new AgentError($"{providerName} ストリーム読み取り失敗: {ex.Message}"); }

        if (error is not null)
        {
            reader?.Dispose(); netStream?.Dispose(); resp!.Dispose();
            yield return error; yield break;
        }

        var toolCalls = new List<ToolUse>();
        var finalText = new StringBuilder();
        AgentError? midError = null;
        var sawAnyModelOutput = false;

        try
        {
            while (true)
            {
                string? line;
                try { line = await reader!.ReadLineAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { midError = new AgentError($"{providerName} ストリーム中断: {ex.Message}"); break; }

                if (line is null) break;            // ストリーム終端
                if (line.Length == 0) continue;     // 空行はスキップ

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch { continue; }                  // 壊れた行はスキップ
                if (node is null) continue;

                var errText = node["error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(errText)) { midError = new AgentError($"{providerName} エラー: {errText}"); break; }

                var message = node["message"];
                if (message is not null)
                {
                    var thinking = message["thinking"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(thinking))
                    {
                        sawAnyModelOutput = true;
                        yield return new ThinkingDelta(thinking);
                    }

                    var content = message["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(content))
                    {
                        sawAnyModelOutput = true;
                        finalText.Append(content);
                        yield return new TextDelta(content);
                    }

                    var calls = message["tool_calls"]?.AsArray();
                    if (calls is not null)
                        foreach (var c in calls)
                        {
                            var fn = c?["function"];
                            if (fn is null) continue;
                            sawAnyModelOutput = true;
                            var name = fn["name"]?.GetValue<string>() ?? "";
                            var args = fn["arguments"];
                            var argsJson = args is null ? "{}" : args.ToJsonString();
                            toolCalls.Add(new ToolUse(Guid.NewGuid().ToString("N"), name, argsJson));
                        }
                }

                if (node["done"]?.GetValue<bool>() == true) break;
            }

            if (midError is null)
            {
                foreach (var tc in toolCalls)
                    yield return new ToolUseRequested(tc);

                if (!sawAnyModelOutput)
                {
                    yield return new AgentError($"{providerName} から応答本文が返りませんでした。モデル名、Ollama の起動状態、BaseUrl を確認してください。");
                    yield break;
                }

                if (toolCalls.Count == 0)
                    yield return new TurnCompleted(finalText.ToString());
            }
        }
        finally
        {
            reader!.Dispose();
            netStream!.Dispose();
            resp!.Dispose();
        }

        if (midError is not null)
            yield return midError;
    }

    /// <summary>エラー応答ボディから <c>{"error":"..."}</c> のメッセージ本体を取り出す（無ければ原文）。</summary>
    private static string ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var msg = node?["error"]?.GetValue<string>();
            return string.IsNullOrEmpty(msg) ? body : msg;
        }
        catch
        {
            return body;
        }
    }
}
