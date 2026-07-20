using System.Collections.Generic;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// モデルの <see cref="ChatFormat"/> に応じて、適切なプロンプトフォーマッタへ振り分ける。
/// OnnxGenAiClient（実ターン）と LocalLlmWarmupService（暖機）の両方が同じ経路でプロンプトを組むことで、
/// KV プレフィックス再利用の前提（暖機文字列＝実ターンの接頭辞）を保つ。
/// </summary>
public static class ChatPrompt
{
    public static string Build(
        ChatFormat format,
        AiSettings settings,
        AgentProfile? profile,
        IReadOnlyList<string> workspaceFolders,
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools)
        => format switch
        {
            ChatFormat.Qwen3 => Qwen3PromptFormatter.Build(settings, profile, workspaceFolders, conversation, tools),
            _ => Phi4PromptFormatter.Build(settings, profile, workspaceFolders, conversation, tools),
        };
}
