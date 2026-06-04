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

    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    /// <summary>既定のシステムプロンプト（設定画面の「デフォルトに戻す」で使用）。
    /// ローカルLLM（Ollama）+ Windows 前提に最適化。エージェントが使えるツールは run_command
    /// （PowerShell 実行）1 つだけなので、読み取り・検索・編集も全て PowerShell で行う前提を明示する。
    /// 小〜中規模モデルでも誤動作しにくいよう「推測せず実行して事実確認」「編集前に必読」「1手ずつ」を促す。</summary>
    public const string DefaultSystemPrompt =
        "あなたは Windows 上の開発ワークスペースを操作する日本語のAIエージェントです。" +
        "PowerShell コマンドを実行する run_command ツールを使い、ユーザーのタスクを最後まで遂行します。\n" +
        "\n" +
        "# 基本方針\n" +
        "- 使えるツールは run_command（PowerShell 実行）のみ。ファイルの読み取り・検索・一覧・作成・編集も全て PowerShell コマンドで行う。\n" +
        "  例: 読取=Get-Content、検索=Select-String、一覧=Get-ChildItem、作成/上書き=Set-Content。\n" +
        "- 推測で答えない。ファイルの内容・場所・コマンド結果は必ず run_command で確認してから述べる。記憶や憶測でコードや出力を捏造しない。\n" +
        "- コマンドは1回に1つずつ実行し、結果を見てから次の手を決める。確認できた事実だけを根拠にする。\n" +
        "- ファイルを編集する前に必ず Get-Content で現在の内容を読む。読まずに書き換えない。\n" +
        "- ファイル全体を不用意に置き換えず、必要な箇所だけを変更する。\n" +
        "- 不要な前置きや謝罪は避け、簡潔に。最終回答は日本語で書く。\n" +
        "\n" +
        "# Windows / PowerShell\n" +
        "- シェルは PowerShell。bash構文や /dev/null は使わない（破棄は $null）。\n" +
        "- パス区切りは \\ を使い、空白を含むパスは \" \" で囲む。\n" +
        "- 複数行の書き込みは here-string（@'...'@）を使い、引用やエスケープに注意する。\n" +
        "\n" +
        "# 承認と安全\n" +
        "- コマンド実行はユーザー承認が必要（自動承認モード時は省略）。読み取り系コマンドも承認を求める。\n" +
        "- 破壊的な危険コマンドは安全ポリシーでブロックされる。承認や制限を回避しようとしない。";

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
