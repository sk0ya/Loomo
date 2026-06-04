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

    /// <summary>ローカルLLM（Ollama ネイティブ API /api/chat）。</summary>
    public ProviderConfig Local { get; set; } = new()
    {
        Model = "llama3.1",
        BaseUrl = "http://localhost:11434"
    };

    public string SystemPrompt { get; set; } =
        "あなたは開発ワークスペースを操作するAIエージェントです。" +
        "list_directory / read_file / open_in_editor / run_command / propose_edit / get_selection の各ツールを使い、" +
        "ターミナルとエディタを駆使してユーザーのタスクを遂行してください。" +
        "ブラウザペインの操作には browser_navigate / browser_read_page / browser_current_url / " +
        "browser_click / browser_type / browser_screenshot を使います（クリック・入力はCSSセレクタで対象を指定します）。" +
        "コマンド実行・ファイル編集・ブラウザの遷移/クリック/入力はユーザー承認が必要です（自動承認モード時は省略されます）。" +
        "破壊的な危険コマンドは安全ポリシーによりブロックされ、ファイルアクセスはワークスペースルート配下に限定されます。";

    public ProviderConfig ConfigFor(AiProvider provider) => Local;
}

public sealed class ProviderConfig
{
    public string Model { get; set; } = "";

    /// <summary>APIキー。実運用では資格情報マネージャ等から注入する想定。</summary>
    public string? ApiKey { get; set; }

    /// <summary>Ollama ホストのベースURL（ローカルLLM等。末尾の /v1 は自動で除去される）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>1応答で生成させる最大トークン数（出力上限）。</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>thinking を有効にするか。Ollama ネイティブ API の <c>think</c> は真偽値で、
    /// 推論量の段階指定（low/medium/high）は無く実質オン/オフのため、bool で持つ。
    /// thinking 非対応モデルでは無視される（<see cref="Clients.ModelProfile.SupportsThinking"/>）。</summary>
    public bool Thinking { get; set; }

    /// <summary>
    /// Ollama に渡す <c>num_ctx</c>（モデルの実行時コンテキスト窓）の上書き。
    /// 0 以下なら <see cref="Clients.ModelProfile.NumCtx"/>（モデル別の推奨値）を使う。
    /// メモリ制約のある環境ではここで小さくできる。この実効値は履歴トリムの上限にも反映される。
    /// </summary>
    public int NumCtx { get; set; }

    /// <summary>
    /// モデルのコンテキストウィンドウ上限（入力+出力）。これを超えないよう送信前に古い履歴を切り詰める。
    /// 実効 <c>num_ctx</c> とこの値の小さい方が実際のトリム上限になる。0以下でトリム無効。
    /// </summary>
    public int MaxContextTokens { get; set; } = 128_000;
}
