using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 会話・ツール定義・システムプロンプトを <b>Phi-4 のチャットテンプレート文字列</b>に組み立てる。
/// 通常メッセージは <c>&lt;|role|&gt;content&lt;|end|&gt;</c>、ツール定義つき system は
/// <c>&lt;|system|&gt;content&lt;|tool|&gt;toolsJson&lt;|/tool|&gt;&lt;|end|&gt;</c>、
/// 末尾は生成開始の <c>&lt;|assistant|&gt;</c> にする
/// （microsoft/Phi-4-mini-instruct の chat_template に準拠）。
/// </summary>
public static class Phi4PromptFormatter
{
    // 非ASCII（日本語パス・本文）を \uXXXX へ化けさせないため relaxed エンコーダを使う。
    // 既定エンコーダだと「アイデア」→「アイデア」となり、可読性とトークン量が悪化する
    // （プロンプト用途で HTML 文脈ではないため relaxed で問題ない）。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Build(
        AiSettings settings,
        AgentProfile? profile,
        IReadOnlyList<string> workspaceFolders,
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools)
    {
        // システムプロンプトは安定要素のみ（検索ガイダンスは環境固定、現在フォルダは準安定）。
        var system = PromptShared.SystemText(settings, profile, workspaceFolders, ChatFormat.Phi4);

        var sb = new StringBuilder();

        sb.Append("<|system|>").Append(system);
        if (tools.Count > 0)
            sb.Append("<|tool|>").Append(ToolBlockJson(tools)).Append("<|/tool|>");
        sb.Append("<|end|>");

        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    sb.Append("<|user|>").Append(PromptShared.UserContent(m)).Append("<|end|>");
                    break;
                case ChatRole.Assistant:
                    sb.Append("<|assistant|>").Append(AssistantContent(m)).Append("<|end|>");
                    break;
                case ChatRole.Tool:
                    // Phi-4 テンプレートでは role "tool" は <|tool|>content<|end|> として描画される。
                    foreach (var r in m.ToolResults)
                        sb.Append("<|tool|>").Append(r.Content).Append("<|end|>");
                    break;
                case ChatRole.System:
                    sb.Append("<|system|>").Append(m.Text ?? "").Append("<|end|>");
                    break;
            }
        }

        sb.Append("<|assistant|>");   // add_generation_prompt
        return sb.ToString();
    }

    /// <summary>アシスタント履歴の本文。過去のツール呼び出しは、Phi-4 の tool-enabled 出力形式で残して一貫性を保つ。</summary>
    private static string AssistantContent(ChatMessage m)
    {
        var text = m.Text ?? "";
        if (m.ToolUses.Count == 0) return text;

        var calls = new JsonArray();
        foreach (var use in m.ToolUses)
        {
            JsonNode args;
            try { args = JsonNode.Parse(use.ArgumentsJson) ?? new JsonObject(); }
            catch { args = new JsonObject(); }
            calls.Add(new JsonObject
            {
                ["name"] = use.Name,
                ["arguments"] = args.DeepClone()
            });
        }
        var callText = calls.ToJsonString(JsonOptions);
        return string.IsNullOrEmpty(text) ? callText : callText + "\n" + text;
    }

    private static string ToolBlockJson(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var tool in tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ToolParameters(tool)
            });
        }
        return arr.ToJsonString(JsonOptions);
    }

    private static JsonNode ToolParameters(ToolDefinition tool) => PromptShared.ToolParameters(tool);
}
