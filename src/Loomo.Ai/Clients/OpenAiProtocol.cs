using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai.Http;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// Ollama の OpenAI Chat Completions 互換プロトコル処理。
/// </summary>
internal static class OpenAiProtocol
{
    /// <summary>会話とツール定義から Chat Completions リクエストボディを組み立てる。</summary>
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
                    if (includeTools)
                    {
                        foreach (var r in m.ToolResults)
                            messages.Add(new JsonObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = r.ToolUseId,
                                ["content"] = r.Content
                            });
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

        var toolArray = new JsonArray();
        if (includeTools)
        {
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
        }

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

    /// <summary>リクエストを送り、応答（テキスト / ツール呼び出し）を一括でイベント化して流す（非ストリーミング）。
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
            using var resp = await HttpRetry.SendAsync(http, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(body)
                };
                configure?.Invoke(req);
                return req;
            }, ct);
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

    /// <summary>SSE（stream:true）で応答を逐次受け取り、届いた分から順にイベント化して流す。
    /// 擬似ストリーミングと違い、思考・本文がリアルタイムに出る。ローカルLLM 用。</summary>
    /// <param name="extractThinking">true なら reasoning_content / &lt;think&gt; タグを
    /// <see cref="ThinkingDelta"/> として分離する（タグはチャンクを跨いでも復元する）。</param>
    public static async IAsyncEnumerable<AgentEvent> SendStreamingAsync(
        HttpClient http,
        string endpoint,
        JsonObject body,
        string providerName,
        Action<HttpRequestMessage>? configure,
        [EnumeratorCancellation] CancellationToken ct,
        bool extractThinking = false)
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
                error = new AgentError($"{providerName} APIエラー {(int)resp.StatusCode}: {errBody}");
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

        var parser = extractThinking ? new ThinkStreamParser() : null;
        var toolCalls = new SortedDictionary<int, StreamToolCall>();
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

                if (line is null) break;                    // ストリーム終端
                if (line.Length == 0) continue;             // イベント区切り
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") break;

                JsonNode? node;
                try { node = JsonNode.Parse(payload); }
                catch { continue; }                          // 壊れた行はスキップ

                var delta = node?["choices"]?[0]?["delta"];
                if (delta is null) continue;

                // 思考（プロバイダにより field 名が揺れる: DeepSeek 系は reasoning_content、
                // Ollama/OpenAI互換のthinkingモデルは reasoning / thinking を返すことがある）
                if (extractThinking)
                {
                    var reasoning = FirstString(delta, "reasoning_content", "reasoning", "thinking");
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        sawAnyModelOutput = true;
                        yield return new ThinkingDelta(reasoning);
                    }
                }

                // 本文（r1 系は <think> タグが埋まるので分離する）
                var content = delta["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(content))
                {
                    if (parser is not null)
                    {
                        foreach (var (isThinking, txt) in parser.Push(content))
                        {
                            if (isThinking) yield return new ThinkingDelta(txt);
                            else { finalText.Append(txt); yield return new TextDelta(txt); }
                            sawAnyModelOutput = true;
                        }
                    }
                    else { finalText.Append(content); yield return new TextDelta(content); sawAnyModelOutput = true; }
                }

                // ツール呼び出し（断片で届くので index ごとに組み立てる）
                var calls = delta["tool_calls"]?.AsArray();
                if (calls is not null)
                    foreach (var c in calls)
                    {
                        var idx = c?["index"]?.GetValue<int>() ?? 0;
                        if (!toolCalls.TryGetValue(idx, out var acc)) { acc = new StreamToolCall(); toolCalls[idx] = acc; }
                        sawAnyModelOutput = true;
                        var id = c?["id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id)) acc.Id = id;
                        var fn = c?["function"];
                        var nm = fn?["name"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(nm)) acc.Name = nm;
                        var ar = fn?["arguments"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(ar)) acc.Args.Append(ar);
                    }
            }

            // 中断時は組み立て途中（不完全なJSON引数など）のツール呼び出しや保留テキストを出さず、
            // エラーだけを通知する。
            if (midError is null)
            {
                // 取りこぼした保留分を吐き出す
                if (parser?.Flush() is { } tail)
                {
                    if (tail.IsThinking) yield return new ThinkingDelta(tail.Text);
                    else { finalText.Append(tail.Text); yield return new TextDelta(tail.Text); }
                    if (!string.IsNullOrEmpty(tail.Text)) sawAnyModelOutput = true;
                }

                foreach (var tc in toolCalls.Values)
                    yield return new ToolUseRequested(new ToolUse(
                        tc.Id ?? Guid.NewGuid().ToString("N"),
                        tc.Name,
                        tc.Args.Length > 0 ? tc.Args.ToString() : "{}"));

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

    /// <summary>ストリーミング中に組み立てるツール呼び出し1件。</summary>
    private sealed class StreamToolCall
    {
        public string? Id;
        public string Name = "";
        public readonly StringBuilder Args = new();
    }

    private static string? FirstString(JsonNode node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name]?.GetValue<string>();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    /// <summary>チャンクを跨いで届く本文から &lt;think&gt;…&lt;/think&gt; を分離するステートフルなパーサ。
    /// タグが途中で切れても、確定できない末尾は次チャンクまで保留する。</summary>
    private sealed class ThinkStreamParser
    {
        private static readonly string[] OpenTokens = { "<think>", "<thinking>" };
        private static readonly string[] CloseTokens = { "</think>", "</thinking>" };

        private bool _inThink;
        private string _buffer = "";

        public IEnumerable<(bool IsThinking, string Text)> Push(string chunk)
        {
            _buffer += chunk;
            var emitted = new List<(bool, string)>();
            while (true)
            {
                var tokens = _inThink ? CloseTokens : OpenTokens;
                var (idx, tokLen) = FindEarliest(_buffer, tokens);
                if (idx >= 0)
                {
                    var before = _buffer[..idx];
                    if (before.Length > 0) emitted.Add((_inThink, before));
                    _buffer = _buffer[(idx + tokLen)..];
                    _inThink = !_inThink;
                    continue;
                }

                // 完全なタグは無い：タグの途中になり得る末尾だけ保留し、残りは確定として出す。
                var hold = LongestPartialSuffix(_buffer, tokens);
                var safeLen = _buffer.Length - hold;
                if (safeLen > 0)
                {
                    emitted.Add((_inThink, _buffer[..safeLen]));
                    _buffer = _buffer[safeLen..];
                }
                break;
            }
            return emitted;
        }

        public (bool IsThinking, string Text)? Flush()
        {
            if (_buffer.Length == 0) return null;
            var r = (_inThink, _buffer);
            _buffer = "";
            return r;
        }

        private static (int Index, int Length) FindEarliest(string buf, string[] tokens)
        {
            int bestIdx = -1, bestLen = 0;
            foreach (var t in tokens)
            {
                var i = buf.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (bestIdx < 0 || i < bestIdx)) { bestIdx = i; bestLen = t.Length; }
            }
            return (bestIdx, bestLen);
        }

        private static int LongestPartialSuffix(string buf, string[] tokens)
        {
            var best = 0;
            foreach (var t in tokens)
            {
                var max = Math.Min(t.Length - 1, buf.Length);
                for (var k = max; k >= 1; k--)
                {
                    if (string.Compare(buf, buf.Length - k, t, 0, k, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        if (k > best) best = k;
                        break;
                    }
                }
            }
            return best;
        }
    }
}
