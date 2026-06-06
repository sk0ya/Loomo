using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 会話・ツール定義・システムプロンプトを <b>Phi-4 のチャットテンプレート文字列</b>に組み立てる。
/// テンプレートは各メッセージを <c>&lt;|role|&gt;content&lt;|end|&gt;</c> で連結し、tools 付き system は
/// <c>&lt;|system|&gt;content&lt;|tool|&gt;[…JSON…]&lt;|/tool|&gt;&lt;|end|&gt;</c>、末尾に生成開始の
/// <c>&lt;|assistant|&gt;</c> を置く（microsoft/Phi-4-mini-instruct の chat_template に準拠）。
/// ツール呼び出しは本文に JSON 配列で返るため、復元は <see cref="ToolCallTextParser"/> が担う。
/// </summary>
public static class Phi4PromptFormatter
{
    public static string Build(
        AiSettings settings,
        AgentProfile? profile,
        string? workspaceRoot,
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools)
    {
        // システムプロンプトは安定要素のみ（検索ガイダンスは環境固定、現在フォルダは準安定）。
        var system = settings.BuildSystemPrompt(profile)
                     + SearchGuidance(workspaceRoot)
                     + WorkspaceContext.Describe(workspaceRoot);

        var sb = new StringBuilder();

        sb.Append("<|system|>").Append(system);
        if (tools.Count > 0)
            sb.Append("<|tool|>").Append(SerializeTools(tools)).Append("<|/tool|>");
        sb.Append("<|end|>");

        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    sb.Append("<|user|>").Append(m.Text ?? "").Append("<|end|>");
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

    /// <summary>アシスタント履歴の本文。過去のツール呼び出しは、モデルが吐く JSON 配列形式で残して一貫性を保つ。</summary>
    private static string AssistantContent(ChatMessage m)
    {
        var text = m.Text ?? "";
        if (m.ToolUses.Count == 0) return text;

        var arr = new JsonArray();
        foreach (var use in m.ToolUses)
        {
            JsonNode args;
            try { args = JsonNode.Parse(use.ArgumentsJson) ?? new JsonObject(); }
            catch { args = new JsonObject(); }
            arr.Add(new JsonObject { ["name"] = use.Name, ["arguments"] = args });
        }
        var calls = arr.ToJsonString();
        return string.IsNullOrEmpty(text) ? calls : text + "\n" + calls;
    }

    /// <summary>ツール定義を Phi-4 が期待する JSON 配列文字列（name/description/parameters）に直す。</summary>
    private static string SerializeTools(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.InputSchema.DeepClone()
            });
        return arr.ToJsonString();
    }

    private static string SearchGuidance(string? workspaceRoot)
        => EnvironmentProbe.HasRipgrep(workspaceRoot)
            ? "\n\nSearch: prefer rg, e.g. rg \"<term>\" <path>, rg --files <path>."
            : "\n\nSearch: use Select-String, e.g. Select-String -Pattern \"<term>\" -Path <path>.";
}
