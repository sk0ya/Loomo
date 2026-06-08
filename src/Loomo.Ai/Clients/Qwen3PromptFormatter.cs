using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 会話・ツール定義・システムプロンプトを <b>Qwen3 の ChatML テンプレート文字列</b>に組み立てる。
/// 通常メッセージは <c>&lt;|im_start|&gt;role\ncontent&lt;|im_end|&gt;</c>、ツール定義は system ターンに
/// Hermes 形式の <c>&lt;tools&gt;…&lt;/tools&gt;</c> ブロックとして注入し、呼び出しは本文に
/// <c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c> で書かせる（Qwen3 公式 chat_template に準拠）。
///
/// <para>thinking は無効化して動かす。生成開始マーカ直後に空の <c>&lt;think&gt;&lt;/think&gt;</c> を
/// プレフィルしておくことで、モデルは推論ブロックを出さず即座にツール呼び出しか最終回答を返す
/// （enable_thinking=false 相当。万一出ても <see cref="ToolCallTextParser"/> が除去する）。</para>
/// </summary>
public static class Qwen3PromptFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string Build(
        AiSettings settings,
        AgentProfile? profile,
        string? workspaceRoot,
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools)
    {
        var system = PromptShared.SystemText(settings, profile, workspaceRoot, ChatFormat.Qwen3);

        var sb = new StringBuilder();

        sb.Append("<|im_start|>system\n").Append(system);
        if (tools.Count > 0)
            sb.Append(ToolsBlock(tools));
        sb.Append("<|im_end|>\n");

        foreach (var m in conversation.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    sb.Append("<|im_start|>user\n").Append(m.Text ?? "").Append("<|im_end|>\n");
                    break;
                case ChatRole.Assistant:
                    sb.Append("<|im_start|>assistant\n").Append(AssistantContent(m)).Append("<|im_end|>\n");
                    break;
                case ChatRole.Tool:
                    // Qwen3 では tool 結果は user ターン内の <tool_response> として描画される。
                    foreach (var r in m.ToolResults)
                        sb.Append("<|im_start|>user\n<tool_response>\n")
                          .Append(r.Content)
                          .Append("\n</tool_response><|im_end|>\n");
                    break;
                case ChatRole.System:
                    sb.Append("<|im_start|>system\n").Append(m.Text ?? "").Append("<|im_end|>\n");
                    break;
            }
        }

        // add_generation_prompt ＋ thinking 無効化（空 think をプレフィル）。
        sb.Append("<|im_start|>assistant\n<think>\n\n</think>\n\n");
        return sb.ToString();
    }

    /// <summary>アシスタント履歴の本文。過去のツール呼び出しは Qwen3 の <c>&lt;tool_call&gt;</c> 形式で残して一貫性を保つ。</summary>
    private static string AssistantContent(ChatMessage m)
    {
        var text = m.Text ?? "";
        if (m.ToolUses.Count == 0) return text;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(text)) sb.Append(text).Append('\n');
        foreach (var use in m.ToolUses)
        {
            JsonNode args;
            try { args = JsonNode.Parse(use.ArgumentsJson) ?? new JsonObject(); }
            catch { args = new JsonObject(); }
            var call = new JsonObject { ["name"] = use.Name, ["arguments"] = args.DeepClone() };
            sb.Append("<tool_call>\n").Append(call.ToJsonString(JsonOptions)).Append("\n</tool_call>\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>system ターンへ注入する Hermes 形式のツール定義ブロック。</summary>
    private static string ToolsBlock(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.Append("\n\n# Tools\n\n")
          .Append("You may call one or more functions to assist with the user query.\n\n")
          .Append("You are provided with function signatures within <tools></tools> XML tags:\n")
          .Append("<tools>\n");
        foreach (var tool in tools)
        {
            var fn = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = PromptShared.ToolParameters(tool),
                }
            };
            sb.Append(fn.ToJsonString(JsonOptions)).Append('\n');
        }
        sb.Append("</tools>\n\n")
          .Append("For each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:\n")
          .Append("<tool_call>\n{\"name\": <function-name>, \"arguments\": <args-json-object>}\n</tool_call>");
        return sb.ToString();
    }
}
