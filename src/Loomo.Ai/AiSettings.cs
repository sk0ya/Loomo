using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Ai;

/// <summary>AIプロバイダ設定。appsettings / ユーザー設定からバインドする。</summary>
public sealed class AiSettings
{
    /// <summary>現在選択中のプロバイダ。</summary>
    public AiProvider Provider { get; set; } = AiProvider.Local;

    /// <summary>UIのカラーテーマ（配色）。既定はダーク。</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>アクセントカラーの上書き（"#RRGGBB" 等）。null/空ならテーマ既定のアクセントを使う。</summary>
    public string? AccentColor { get; set; }

    /// <summary>コマンド実行・書込の安全設計（設計書 §10）。</summary>
    public SafetySettings Safety { get; set; } = new();

    /// <summary>AI操作トレース（観測性・設計書 §20）の設定。</summary>
    public ObservabilitySettings Observability { get; set; } = new();

    /// <summary>ローカルLLM（Ollama OpenAI互換エンドポイント）。</summary>
    public ProviderConfig Local { get; set; } = new()
    {
        Model = "llama3.1",
        BaseUrl = "http://localhost:11434/v1"
    };

    public string SystemPrompt { get; set; } =
        "あなたは開発ワークスペースを操作するAIエージェントです。" +
        "list_directory / read_file / open_in_editor / run_command / propose_edit / get_selection の各ツールを使い、" +
        "ターミナルとエディタを駆使してユーザーのタスクを遂行してください。" +
        "コマンド実行とファイル編集はユーザー承認が必要です（自動承認モード時は省略されます）。" +
        "破壊的な危険コマンドは安全ポリシーによりブロックされ、ファイルアクセスはワークスペースルート配下に限定されます。";

    public ProviderConfig ConfigFor(AiProvider provider) => Local;
}

public sealed class ProviderConfig
{
    public string Model { get; set; } = "";

    /// <summary>APIキー。実運用では資格情報マネージャ等から注入する想定。</summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenAI互換エンドポイントのベースURL（ローカルLLM等）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>1応答で生成させる最大トークン数（出力上限）。</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// モデルのコンテキストウィンドウ上限（入力+出力）。これを超えないよう送信前に古い履歴を切り詰める。
    /// 0以下でトリム無効。既定は 128k 級モデル想定。
    /// </summary>
    public int MaxContextTokens { get; set; } = 128_000;
}
