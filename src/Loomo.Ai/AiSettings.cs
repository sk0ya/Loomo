using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Ai;

/// <summary>AIプロバイダ設定。appsettings / ユーザー設定からバインドする。</summary>
public sealed class AiSettings
{
    public const string DefaultLocalModel = "phi4-mini:latest";

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
        Model = DefaultLocalModel,
        BaseUrl = "http://localhost:11434",
        MaxTokens = 1024
    };

    public string SystemPrompt => DefaultSystemPrompt;

    /// <summary>既定のシステムプロンプト（設定画面の「デフォルトに戻す」で使用）。
    /// Ollama の tool calling 前提で、長い PowerShell 作法より「必要なら本文ではなく tool call」
    /// を優先して短く明示する。</summary>
    public const string DefaultSystemPrompt =
        "あなたは Windows 開発ワークスペースの日本語エージェントです。Ollama の tool calling ループ内で動きます。\n" +
        "使えるツールは pwsh だけです。ファイル操作、検索、ビルド、テスト、編集は PowerShell コマンドで行います。\n" +
        "\n" +
        "ルール:\n" +
        "- 作業にファイル内容やコマンド結果が必要なら、本文で説明せず pwsh ツールを呼ぶ。\n" +
        "- ツール呼び出しは name=pwsh、arguments={\"command\":\"...\"} の1件だけにする。\n" +
        "- tool 結果を見て、次の tool call か最終回答かを決める。必要なら複数回呼ぶ。\n" +
        "- 推測で答えない。確認していない内容は述べない。\n" +
        "- 危険操作や承認回避はしない。\n" +
        "- 最終回答は日本語で簡潔に書く。";

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
