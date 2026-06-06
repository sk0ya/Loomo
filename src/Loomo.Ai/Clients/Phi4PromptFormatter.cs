using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 会話・ツール定義・システムプロンプトを <b>Phi-4 のチャットテンプレート文字列</b>に組み立てる。
/// テンプレートは各メッセージを <c>&lt;|role|&gt;content&lt;|end|&gt;</c> で連結し、末尾に生成開始の
/// <c>&lt;|assistant|&gt;</c> を置く（microsoft/Phi-4-mini-instruct の chat_template に準拠）。
/// ローカル小型モデルには tool 定義 JSON を渡すと定義そのものを出力しやすいため、
/// ツール呼び出しは唯一の tool の引数 JSON <c>{"command":"..."}</c> として本文に書かせる。
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
            sb.Append(ToolArgumentGuidance(tools));
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

    /// <summary>アシスタント履歴の本文。過去のツール呼び出しは、モデルに期待する引数 JSON 形式で残して一貫性を保つ。</summary>
    private static string AssistantContent(ChatMessage m)
    {
        var text = m.Text ?? "";
        if (m.ToolUses.Count == 0) return text;

        var calls = new List<string>();
        foreach (var use in m.ToolUses)
        {
            JsonNode args;
            try { args = JsonNode.Parse(use.ArgumentsJson) ?? new JsonObject(); }
            catch { args = new JsonObject(); }
            calls.Add(args.ToJsonString());
        }
        var callText = string.Join("\n", calls);
        return string.IsNullOrEmpty(text) ? callText : text + "\n" + callText;
    }

    private static string ToolArgumentGuidance(IReadOnlyList<ToolDefinition> tools)
    {
        // Only run_powershell is registered today. Avoid exposing name/description/parameters here:
        // local small models tend to copy tool definitions instead of emitting arguments.
        var tool = tools[0];
        var commandDescription = "PowerShell command";
        if (tool.InputSchema["properties"] is JsonObject props
            && props["command"] is JsonObject command
            && command["description"] is JsonValue description
            && description.TryGetValue<string>(out var value)
            && !string.IsNullOrWhiteSpace(value))
            commandDescription = value;

        return "\n\nTool output format: when you need PowerShell, output exactly one JSON object and no prose. " +
               "The first character must be { and the last must be }. Format: " +
               "{\"command\":\"<" + commandDescription + ">\"}.";
    }

    private static string SearchGuidance(string? workspaceRoot)
        => EnvironmentProbe.HasRipgrep(workspaceRoot)
            ? "\n\nSearch: prefer rg, e.g. rg \"<term>\" <path>, rg --files <path>."
            : "\n\nSearch: use Select-String, e.g. Select-String -Pattern \"<term>\" -Path <path>.";
}
