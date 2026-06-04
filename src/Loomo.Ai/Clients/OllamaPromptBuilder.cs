using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>Ollama に渡す安定 system プロンプトを組み立てる。</summary>
public static class OllamaPromptBuilder
{
    public static string Build(AiSettings settings, AgentProfile? profile, string? workspaceRoot)
    {
        var cfg = settings.Local;
        var modelProfile = ModelProfiles.Resolve(cfg.Model);
        return settings.BuildSystemPrompt(profile)
               + modelProfile.StyleGuidance
               + SearchGuidance(workspaceRoot);
    }

    private static string SearchGuidance(string? workspaceRoot)
        => EnvironmentProbe.HasRipgrep(workspaceRoot)
            ? "\n\n検索: rg を優先。例: rg \"<語>\" <パス>、rg --files <パス>。"
            : "\n\n検索: Select-String を使う。例: Select-String -Pattern \"<語>\" -Path <パス>。";
}
