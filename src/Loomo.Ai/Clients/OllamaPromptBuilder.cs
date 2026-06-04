using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>Ollama に渡す安定 system プロンプトを組み立てる。</summary>
public static class OllamaPromptBuilder
{
    public static string Build(AiSettings settings, AgentProfile? profile, string? workspaceRoot)
    {
        // システムプロンプトは全モデル共通（モデル固有の差し込みはしない）。検索ガイダンスだけ
        // 環境（rg の有無）で決まる固定値なので、安定プレフィックスに含めて差し支えない。
        return settings.BuildSystemPrompt(profile)
               + SearchGuidance(workspaceRoot);
    }

    private static string SearchGuidance(string? workspaceRoot)
        => EnvironmentProbe.HasRipgrep(workspaceRoot)
            ? "\n\nSearch: prefer rg, e.g. rg \"<term>\" <path>, rg --files <path>."
            : "\n\nSearch: use Select-String, e.g. Select-String -Pattern \"<term>\" -Path <path>.";
}
