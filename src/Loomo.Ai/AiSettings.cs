using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Ai;

/// <summary>AIプロバイダ設定。appsettings / ユーザー設定からバインドする。</summary>
public sealed class AiSettings
{
    public const string DefaultLocalModel = "qwen2.5-coder:3b";

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
        MaxTokens = 1024
    };

    public string SystemPrompt => DefaultSystemPrompt;

    public string BuildSystemPrompt(AgentProfile? profile = null)
        => (profile ?? AgentProfiles.Root).ApplyTo(SystemPrompt);

    /// <summary>既定のシステムプロンプト（設定画面の「デフォルトに戻す」で使用）。
    /// Ollama の tool calling 前提で、長い PowerShell 作法より「必要なら本文ではなく tool call」
    /// を優先して短く明示する。</summary>
    public const string DefaultSystemPrompt =
        "You are a Japanese-speaking agent for a Windows dev workspace, inside an Ollama tool-calling loop. " +
        "The only tool is pwsh; do all file ops, search, build, test, and edits as PowerShell.\n" +
        "Rules:\n" +
        "- If you need file contents or command output, call the pwsh tool, don't explain in prose.\n" +
        "- Each call: arguments={\"command\":\"...\"} with one command.\n" +
        "- Read the result, then choose the next tool call or final answer; repeat if needed.\n" +
        "- Don't state anything unverified. Final answer in Japanese, concise: " +
        "answer directly, no preamble or follow-up offers.";

    public ProviderConfig ConfigFor(AiProvider provider) => Local;
}

public sealed class ProviderConfig
{
    public string Model { get; set; } = "";

    /// <summary>APIキー。実運用では資格情報マネージャ等から注入する想定。</summary>
    public string? ApiKey { get; set; }

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

    /// <summary>
    /// Ollama に渡す <c>num_gpu</c>（GPU へオフロードするレイヤー数）の上書き。<b>負値（既定 -1）なら送らず</b>、
    /// Ollama の自動判定に任せる。<c>0</c> で GPU オフロードを完全に無効化（100% CPU 実行）。
    /// 用途: VRAM の小さい貧弱な GPU（例: GT 710）にごく一部のレイヤーが載ると、その GPU と PCIe が
    /// prefill のボトルネックになり CPU 単独より<b>桁違いに遅くなる</b>ことがある（実測 prefill 約6→39 tok/s）。
    /// そうした環境では 0 にして CPU 実行へ寄せると速い。まともな GPU があるマシンでは負値のままにする。
    /// </summary>
    public int NumGpu { get; set; } = -1;
}
