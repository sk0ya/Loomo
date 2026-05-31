using AgentStudio.Core.Models;

namespace AgentStudio.Ai;

/// <summary>AIプロバイダ設定。appsettings / ユーザー設定からバインドする。</summary>
public sealed class AiSettings
{
    /// <summary>現在選択中のプロバイダ。</summary>
    public AiProvider Provider { get; set; } = AiProvider.Stub;

    public ProviderConfig Claude { get; set; } = new() { Model = "claude-opus-4-8" };
    public ProviderConfig OpenAI { get; set; } = new() { Model = "gpt-4o" };
    public ProviderConfig Copilot { get; set; } = new() { Model = "gpt-4o" };

    /// <summary>ローカルLLM（Ollama 等 OpenAI互換エンドポイント）。</summary>
    public ProviderConfig Local { get; set; } = new()
    {
        Model = "llama3.1",
        BaseUrl = "http://localhost:11434/v1"
    };

    public string SystemPrompt { get; set; } =
        "あなたは開発ワークスペースを操作するAIエージェントです。" +
        "list_directory / read_file / open_in_editor / run_command / propose_edit / get_selection の各ツールを使い、" +
        "ターミナルとエディタを駆使してユーザーのタスクを遂行してください。" +
        "コマンド実行とファイル編集はユーザー承認が必要です。";

    public ProviderConfig ConfigFor(AiProvider provider) => provider switch
    {
        AiProvider.Claude => Claude,
        AiProvider.OpenAI => OpenAI,
        AiProvider.Copilot => Copilot,
        AiProvider.Local => Local,
        _ => new ProviderConfig()
    };
}

public sealed class ProviderConfig
{
    public string Model { get; set; } = "";

    /// <summary>APIキー。実運用では資格情報マネージャ等から注入する想定。</summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenAI互換エンドポイントのベースURL（ローカルLLM等）。</summary>
    public string? BaseUrl { get; set; }

    public int MaxTokens { get; set; } = 4096;
}
