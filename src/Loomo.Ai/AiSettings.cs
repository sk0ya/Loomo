using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Ai;

/// <summary>AIプロバイダ設定。appsettings / ユーザー設定からバインドする。</summary>
public sealed class AiSettings
{
    public const string DefaultLocalModel = "phi4-mini";

    /// <summary>現在選択中のプロバイダ。</summary>
    public AiProvider Provider { get; set; } = AiProvider.Local;

    /// <summary>UIのカラーテーマ（配色）。既定はダーク。</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>AIウォームアップを有効にするか。既定は有効。
    /// 有効なら起動時／ワークスペース確定時に Phi-4-mini をロードし、system プロンプト＋ツール定義の
    /// 安定プレフィックスを常駐 Generator へ prefill して KV キャッシュを温める（初回ターンの prefill を
    /// 払い直さず体感が速くなる）。暖機の実行中は AI への指示を受け付けない
    /// （<see cref="Clients.Phi4Engine"/> がモデルロード・prefill 中で占有されるため）。
    /// 無効にすると暖機を一切行わず、最初のAIターンで通常どおりロード／prefill する。</summary>
    public bool WarmupEnabled { get; set; } = true;

    /// <summary>アクセントカラーの上書き（"#RRGGBB" 等）。null/空ならテーマ既定のアクセントを使う。</summary>
    public string? AccentColor { get; set; }

    /// <summary>コマンド実行・書込の安全設計（設計書 §10）。</summary>
    public SafetySettings Safety { get; set; } = new();

    /// <summary>AI操作トレース（観測性・設計書 §20）の設定。</summary>
    public ObservabilitySettings Observability { get; set; } = new();

    /// <summary>埋め込み Vim エディタの設定。</summary>
    public VimSettings Vim { get; set; } = new();

    /// <summary>エディタ／Markdownプレビュー／ターミナルの配色・フォント設定。
    /// アプリUIの配色（<see cref="Theme"/>/<see cref="AccentColor"/>）とは独立に各コンポーネントへ適用する。</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>ローカルLLM（ONNX Runtime GenAI・in-process／CPU）。</summary>
    public ProviderConfig Local { get; set; } = new()
    {
        Model = DefaultLocalModel,
        MaxTokens = 1024
    };

    public string SystemPrompt => DefaultSystemPrompt;

    public string BuildSystemPrompt(AgentProfile? profile = null)
        => (profile ?? AgentProfiles.Root).ApplyTo(SystemPrompt);

    /// <summary>既定のシステムプロンプト（設定画面の「デフォルトに戻す」で使用）。
    /// ローカルLLM の tool calling 前提で、構文契約と最小限の作業方針だけを明示する。
    /// 個々のツール説明は <c>&lt;|tool|&gt;</c> ブロックへ出るため、重複させず prefill を抑える。</summary>
    public const string DefaultSystemPrompt =
        "You are a Japanese-speaking agent in a Windows dev workspace, inside a tool-calling loop.\n" +
        "Choose one mode per reply: tool call OR final answer, never both.\n" +
        "Tool call mode: output only valid JSON array [{\"name\":\"<tool>\",\"arguments\":{...}}]. First char [; last char ]. No prose, schema, empty args, or unescaped quotes.\n" +
        "Use exact tool names and argument keys from the tool schema. Do not write code fences or run_powershell(\"...\").\n" +
        "If the user asks about files, commands, builds, tests, git status/diff, or any current workspace state, you MUST use a tool first; do not answer from memory.\n" +
        "For shell, read, search, list, build, and test tasks use run_powershell with a non-empty command, e.g. [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}].\n" +
        "PowerShell commands must be complete and non-interactive. Prefer rg/Get-ChildItem/Get-Content/Select-String/dotnet. Avoid prompts, pagers, interactive editors, and bare cd.\n" +
        "Use write_file for new/full file writes. Use edit_file for exact unique replacements after reading the file. Do not put multi-line file content inside a PowerShell command.\n" +
        "Use multiple objects only for INDEPENDENT operations. Dependent work MUST be separate steps; when unsure, emit one call and wait.\n" +
        "Before edit_file, read the file first so old_string matches exactly. Verify important facts with tools.\n" +
        "Final answer mode: plain concise Japanese text, no JSON.";

    public ProviderConfig ConfigFor(AiProvider provider) => Local;
}

public sealed class ProviderConfig
{
    public string Model { get; set; } = "";

    /// <summary>ローカル推論エンジン（ONNX Runtime GenAI）が読む ONNX モデルフォルダの絶対パス。
    /// <c>genai_config.json</c> ＋ <c>*.onnx</c> ＋ tokenizer 一式を含むフォルダ
    /// （例: <c>microsoft/Phi-4-mini-instruct-onnx</c> の CPU int4 バリアント）。空なら未設定。</summary>
    public string ModelPath { get; set; } = "";

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

public sealed class VimSettings
{
    /// <summary>
    /// 埋め込みエディタで Vim キーバインドを有効にする。
    /// </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>エディタ／Markdownプレビュー／ターミナルの配色・フォント設定。
/// テーマ名はコンポーネントごとに使えるプリセット名（UI 適用時に解決する）。</summary>
public sealed class AppearanceSettings
{
    /// <summary>エディタの配色テーマ。<c>Dracula / Dark / Nord / TokyoNight / OneDark</c>。</summary>
    public string EditorTheme { get; set; } = "Dracula";

    /// <summary>エディタのフォントファミリ。null/空ならコントロール既定。</summary>
    public string? EditorFontFamily { get; set; }

    /// <summary>エディタのフォントサイズ。0 以下ならコントロール既定。</summary>
    public double EditorFontSize { get; set; }

    /// <summary>Markdownプレビューの配色テーマ。<c>Dracula / Dark / Light / GitHub</c>。</summary>
    public string MarkdownPreviewTheme { get; set; } = "Dracula";

    /// <summary>ターミナルの配色テーマ（背景/文字色のプリセット）。<c>Dark / Light / Dracula / Nord / SolarizedDark</c>。</summary>
    public string TerminalTheme { get; set; } = "Dark";

    /// <summary>ターミナルのフォントファミリ。null/空ならコントロール既定。</summary>
    public string? TerminalFontFamily { get; set; }

    /// <summary>ターミナルのフォントサイズ。0 以下ならコントロール既定。</summary>
    public double TerminalFontSize { get; set; }
}
