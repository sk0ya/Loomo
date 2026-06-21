using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// チャットテンプレートに依存しない共有部品。システムプロンプト本文（行動規約＋検索ガイダンス＋
/// 現在フォルダ）と、ツール定義の <c>parameters</c> スキーマ取り出しを集約し、Phi-4／Qwen3 双方の
/// フォーマッタから使う。<see cref="ChatFormat"/> によって書式依存の例文だけが切り替わる。
/// </summary>
internal static class PromptShared
{
    /// <summary>システムプロンプト本文を組み立てる。安定要素のみ（行動規約は固定、検索ガイダンスは環境固定、
    /// 現在フォルダは準安定）。<paramref name="format"/> で tool 呼び出しの記法に合った例文を選ぶ。</summary>
    public static string SystemText(AiSettings settings, AgentProfile? profile, string? workspaceRoot, ChatFormat format)
        => settings.BuildSystemPrompt(profile, format)
           + SearchGuidance(workspaceRoot)
           + WorkspaceContext.Describe(workspaceRoot);

    /// <summary>ユーザーターンの描画本文。<see cref="ChatMessage.RenderPrefix"/>（モード別の追加プロンプト）が
    /// あれば本文の前へ連結する。共有 system プレフィックスより後ろ（user ターン）に入るため KV 共有を壊さない。</summary>
    public static string UserContent(ChatMessage m)
    {
        var text = m.Text ?? "";
        return string.IsNullOrEmpty(m.RenderPrefix) ? text : m.RenderPrefix + "\n\n" + text;
    }

    /// <summary>ツール定義の <c>parameters</c>（JSON Schema の properties 部）を取り出す。</summary>
    public static JsonNode ToolParameters(ToolDefinition tool)
        => tool.InputSchema["properties"]?.DeepClone() ?? tool.InputSchema.DeepClone();

    /// <summary>ripgrep の有無に応じた検索ガイダンス。</summary>
    public static string SearchGuidance(string? workspaceRoot)
        => EnvironmentProbe.HasRipgrep(workspaceRoot)
            ? "\n\nSearch: prefer rg, e.g. rg \"<term>\" <path>, rg --files <path>."
            : "\n\nSearch: use Select-String, e.g. Select-String -Pattern \"<term>\" -Path <path>.";
}
