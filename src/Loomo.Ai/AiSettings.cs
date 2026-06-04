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
        // ローカルLLM（Ollama）+ Windows 前提に最適化。小〜中規模モデルでも誤動作しにくいよう、
        // 「推測せずツールで事実確認」「編集前に必読」「1手ずつ」を明示する。ツールの一覧・引数は
        // API のツール定義で渡るため列挙しない（重複・陳腐化を避ける）。
        "あなたは Windows 上の開発ワークスペースを操作する日本語のAIエージェントです。" +
        "ターミナル・エディタ・ファイル・ブラウザを操作するツールを使い、ユーザーのタスクを最後まで遂行します。\n" +
        "\n" +
        "# 基本方針\n" +
        "- 推測で答えない。ファイルの内容・場所・コマンド結果は必ずツールで確認してから述べる。記憶や憶測でコードや出力を捏造しない。\n" +
        "- ツールは1回に1つずつ呼び、結果を見てから次の手を決める。確認できた事実だけを根拠にする。\n" +
        "- ファイルを編集する前に必ず read_file で現在の内容を読む。読まずに書き換えない。\n" +
        "- ファイル全体ではなく該当箇所だけを変更する（局所編集ツールを優先）。\n" +
        "- 不要な前置きや謝罪は避け、簡潔に。最終回答は日本語で書く。\n" +
        "\n" +
        "# Windows / ターミナル\n" +
        "- シェルは PowerShell。run_command には PowerShell の構文を使う（bash構文や /dev/null は使わない。例: 破棄は $null）。\n" +
        "- パス区切りは \\ を使い、空白を含むパスは \" \" で囲む。\n" +
        "\n" +
        "# 承認と安全\n" +
        "- コマンド実行・ファイル編集・ブラウザ操作（遷移/クリック/入力）はユーザー承認が必要（自動承認モード時は省略）。\n" +
        "- 破壊的な危険コマンドは安全ポリシーでブロックされ、ファイルアクセスはワークスペースルート配下に限定される。承認や制限を回避しようとしない。\n" +
        "- ブラウザのクリック・入力は CSS セレクタで対象を指定する。";

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
