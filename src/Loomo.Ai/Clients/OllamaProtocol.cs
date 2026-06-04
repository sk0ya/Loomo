using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    /// <summary>モデルをメモリに常駐させ続ける時間（秒）。-1 は無期限（Ollama 仕様）。
    /// ターン間・セッション間で再ロードされると毎回コールド起動になり、プレフィックスの KV キャッシュ
    /// （巨大なツール定義の prefill 結果）も失われて遅くなる。CPU 実行ではコールドの prefill が支配的
    /// （モデル重みのページインで数十秒）なので、無期限常駐にしてコールドを「初回 1 回だけ」に抑える。</summary>
    private const int KeepAlive = -1;
    private static readonly Regex LooseToolCall = new(
        @"^\s*(?:name\s*=\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{(?<body>.*)\}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex LooseProperty = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*""(?<value>(?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>会話とツール定義から /api/chat リクエストボディを組み立てる。</summary>
    /// <param name="workspaceContext">毎ターン変わる「現在のフォルダ」等の揮発的な文脈。
    /// システムプロンプト（安定プレフィックス）には載せず、最新ユーザーメッセージの末尾へ添える。
    /// これにより system＋tools の巨大プレフィックスの KV キャッシュが再利用され prefill が省ける。</param>
    public static JsonObject BuildRequest(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        string model,
        int maxTokens,
        string systemPrompt,
        bool includeTools = true,
        bool wantThink = false,
        int numCtxOverride = 0,
        string workspaceContext = "")
    {
        // モデル別プロファイルで thinking / サンプリング / num_ctx を最適化する。
        var profile = ModelProfiles.Resolve(model);
        var thinking = wantThink && profile.SupportsThinking;
        // 呼び出し側が許可していても、モデルが tools 非対応なら送らない（送るとエラーになる）。
        var sendTools = includeTools && profile.SupportsTools;

        if (!sendTools && tools.Count > 0)
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

        // 揮発的な workspaceContext を末尾へ添える対象（最新の user メッセージ）。
        JsonObject? lastUserMsg = null;
        // 直前のアシスタントが「生本文を逐語再生」したか。逐語再生時は tool_calls を出さないので、
        // 続くツール結果は role:"tool" ではなく素の user メッセージとして積む（生成列の自然な拡張にする）。
        var lastAssistantWasVerbatim = false;

        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    var userMsg = new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" };
                    messages.Add(userMsg);
                    lastUserMsg = userMsg;
                    break;

                case ChatRole.Assistant:
                    // モデルが本文テキストとして吐いたツール呼び出しは、その生本文を逐語で積み直す。
                    // tool_calls へ再構成するとスロットの生成トークン列と食い違い、プレフィックスKV再利用が
                    // 効かず会話全体が再 prefill される。逐語ならツール有りでも厳密拡張になり再利用が効く。
                    if (m.ProviderContent is not null)
                    {
                        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.ProviderContent });
                        lastAssistantWasVerbatim = true;
                        break;
                    }

                    lastAssistantWasVerbatim = false;
                    var assistantText = m.Text ?? "";
                    if (!sendTools && m.ToolUses.Count > 0)
                        assistantText = AppendToolUseSummary(assistantText, m.ToolUses);

                    var asMsg = new JsonObject { ["role"] = "assistant", ["content"] = assistantText };
                    if (sendTools && m.ToolUses.Count > 0)
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
                    if (sendTools && !lastAssistantWasVerbatim)
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
                        // 逐語再生の直後、またはツール非対応モデルでは、結果を素の user テキストとして積む。
                        // 逐語再生パスでは GUID や status の足場を入れない簡潔形式にする：足場文字列は
                        // プレフィックスKVキャッシュの再利用を妨げる組み合わせを生むことがあり（実測）、
                        // モデルにとってもノイズなので、結果本文だけを積んで再利用を最大化する。
                        var content = lastAssistantWasVerbatim
                            ? BuildPlainToolResult(m.ToolResults)
                            : BuildToolResultSummary(m.ToolResults);
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    }
                    lastAssistantWasVerbatim = false;
                    break;
            }
        }

        // 揮発的な文脈は安定プレフィックス（system）ではなく最新 user メッセージ末尾へ。
        // system＋tools のプレフィックスが byte 安定になり、Ollama がその KV キャッシュを再利用できる。
        if (!string.IsNullOrEmpty(workspaceContext))
        {
            if (lastUserMsg is not null)
                lastUserMsg["content"] = (lastUserMsg["content"]?.GetValue<string>() ?? "") + workspaceContext;
            else
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = workspaceContext });
        }

        var numPredict = profile.MaxOutputTokens > 0
            ? Math.Min(maxTokens, profile.MaxOutputTokens)
            : maxTokens;
        var options = new JsonObject { ["num_predict"] = numPredict };
        // 実効コンテキスト窓（設定の上書き優先・無ければプロファイル既定）。トリム予算もこの値に揃える。
        var numCtx = numCtxOverride > 0 ? numCtxOverride : profile.NumCtx;
        if (numCtx > 0)
            options["num_ctx"] = numCtx;                    // Ollama 既定(4096)はエージェント用途に狭いため広げる
        profile.SamplingFor(thinking).ApplyTo(options);     // モデルファミリ別の推奨サンプリング

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["options"] = options
        };

        // think は常に送る。thinking は (wantThink && SupportsThinking) なので非対応モデルへ true が行くことはなく、
        // オフ時の think:false はどのモデルでも無害で、既定で思考する未知モデルも確実に黙らせられる。
        body["think"] = thinking;

        // モデルを常駐させ、ターン間の再ロードとプレフィックス KV キャッシュの喪失を防ぐ。
        body["keep_alive"] = KeepAlive;

        if (sendTools && tools.Count > 0)
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

    /// <summary>逐語再生パス用のツール結果。GUID/statusの足場を入れず結果本文だけを簡潔に積む
    /// （プレフィックスKVキャッシュの再利用を妨げにくく、モデルにもノイズが少ない）。</summary>
    private static string BuildPlainToolResult(IReadOnlyList<ToolResultMessage> results)
    {
        var sb = new StringBuilder("ツール実行結果:");
        foreach (var r in results)
            sb.Append('\n').Append(r.Content);
        return sb.ToString();
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
        var allowedToolNames = ToolNamesFromBody(body);
        var mayUseTools = allowedToolNames.Count > 0;

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
        AiUsageReported? usage = null;

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
                        if (!mayUseTools)
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

                // 最終 done 行にトークン数・段階別の所要（ナノ秒）が載る。切り分け計測のため拾う。
                if (node["done"]?.GetValue<bool>() == true)
                {
                    usage = ParseUsage(node);
                    break;
                }
            }

            if (midError is null)
            {
                // 利用統計はモデル出力の有無に関わらず、まず通知する（記録目的）。
                if (usage is not null)
                    yield return usage;

                // ツール呼び出しを本文テキストとして吐いたモデルの生本文。履歴へ逐語で積み直すため保持する。
                string? contentDerivedRaw = null;
                if (toolCalls.Count == 0 && mayUseTools)
                {
                    var contentToolCall = TryParseContentToolCall(finalText.ToString(), allowedToolNames);
                    if (contentToolCall is not null)
                    {
                        toolCalls.Add(contentToolCall);
                        contentDerivedRaw = finalText.ToString();
                    }
                    else if (finalText.Length > 0)
                        yield return new TextDelta(finalText.ToString());
                }

                // 生本文を先に通知（オーケストレーターが ProviderContent に保存）。次ターンの逐語再生で
                // Ollama のプレフィックスKVキャッシュが効き、ツール往復後の全再 prefill を避けられる。
                if (contentDerivedRaw is not null)
                    yield return new AssistantContentCaptured(contentDerivedRaw);

                foreach (var tc in toolCalls)
                    yield return new ToolUseRequested(tc);

                if (!sawAnyModelOutput)
                {
                    yield return new AgentError($"{providerName} から応答本文が返りませんでした。モデル名と Ollama の起動状態を確認してください。");
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

    /// <summary>
    /// 最終 <c>done</c> 行から利用統計を取り出す。トークン数（<c>prompt_eval_count</c> /
    /// <c>eval_count</c>）と段階別の所要をまとめる。Ollama の duration はナノ秒なので ms に直す。
    /// 値が欠けていても落ちないよう、各項目は null 許容で読む。全項目欠落なら null を返す。
    /// </summary>
    private static AiUsageReported? ParseUsage(JsonNode done)
    {
        var input = ReadLong(done, "prompt_eval_count");
        var output = ReadLong(done, "eval_count");
        var loadMs = NanosToMs(ReadLong(done, "load_duration"));
        var promptMs = NanosToMs(ReadLong(done, "prompt_eval_duration"));
        var evalMs = NanosToMs(ReadLong(done, "eval_duration"));
        var totalMs = NanosToMs(ReadLong(done, "total_duration"));

        if (input is null && output is null && loadMs is null &&
            promptMs is null && evalMs is null && totalMs is null)
            return null;

        return new AiUsageReported(input, output, loadMs, promptMs, evalMs, totalMs);
    }

    private static long? ReadLong(JsonNode node, string name)
    {
        var v = node[name];
        if (v is null) return null;
        try { return v.GetValue<long>(); }
        catch { return null; }
    }

    private static double? NanosToMs(long? nanos)
        => nanos is null ? null : nanos.Value / 1_000_000.0;

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

    private static HashSet<string> ToolNamesFromBody(JsonObject body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tools = body["tools"]?.AsArray();
        if (tools is null) return names;

        foreach (var tool in tools)
        {
            var name = tool?["function"]?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    private static ToolUse? TryParseContentToolCall(string content, IReadOnlySet<string> allowedToolNames)
    {
        var json = ExtractJsonValue(content);
        if (json is null) return TryParseLooseToolCall(content, allowedToolNames);

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return null; }

        return TryBuildToolUseFromJson(node, allowedToolNames);
    }

    private static ToolUse? TryBuildToolUseFromJson(JsonNode? node, IReadOnlySet<string> allowedToolNames)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var use = TryBuildToolUseFromJson(item, allowedToolNames);
                if (use is not null) return use;
            }
            return null;
        }

        var obj = node as JsonObject;
        if (obj is null) return null;

        // Native tool call ではなく本文に
        // [{"type":"function","function":{"name":"pwsh",...},"parameters":{"command":"..."}}]
        // のような JSON を返す phi4-mini の実応答を tool use として扱う。
        var fn = obj["function"] as JsonObject;
        var name = fn?["name"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || !allowedToolNames.Contains(name))
            return null;

        var args = fn?["arguments"] ?? obj["arguments"] ?? obj["parameters"];
        if (args is null && obj["command"]?.GetValue<string>() is { Length: > 0 } command)
            args = new JsonObject { ["command"] = command };
        var argsJson = args is null ? "{}" : args.ToJsonString();
        return new ToolUse(Guid.NewGuid().ToString("N"), name, argsJson);
    }

    private static ToolUse? TryParseLooseToolCall(string content, IReadOnlySet<string> allowedToolNames)
    {
        var match = LooseToolCall.Match(content.Trim());
        if (!match.Success) return null;

        var name = match.Groups["name"].Value;
        if (!allowedToolNames.Contains(name)) return null;

        var bodyJson = "{" + match.Groups["body"].Value + "}";
        try
        {
            var body = JsonNode.Parse(bodyJson);
            if (body is JsonObject obj && obj["command"]?.GetValue<string>() is { Length: > 0 })
                return new ToolUse(Guid.NewGuid().ToString("N"), name, obj.ToJsonString());
        }
        catch
        {
            // phi4-mini は command: "..." のような非 JSON 形式も返すため、下の緩い抽出へ進む。
        }

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match prop in LooseProperty.Matches(match.Groups["body"].Value))
            props[prop.Groups["key"].Value] = Regex.Unescape(prop.Groups["value"].Value);

        if (!props.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
            return null;

        if (props.TryGetValue("arguments", out var argument) &&
            !string.IsNullOrWhiteSpace(argument) &&
            !command.Contains(' ', StringComparison.Ordinal))
        {
            command += " " + QuotePowerShellArgument(argument);
        }

        var argsJson = JsonSerializer.Serialize(new { command });
        return new ToolUse(Guid.NewGuid().ToString("N"), name, argsJson);
    }

    private static string QuotePowerShellArgument(string value)
        => "'" + value.Replace("'", "''") + "'";

    private static string? ExtractJsonValue(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }

        if ((text.StartsWith('{') && text.EndsWith('}')) ||
            (text.StartsWith('[') && text.EndsWith(']')))
            return text;

        return null;
    }
}
