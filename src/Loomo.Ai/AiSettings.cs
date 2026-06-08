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

    /// <summary>チャット記法に合わせたシステムプロンプトを組み立てる。Qwen3（ChatML/Hermes tool call）と
    /// Phi-4（JSON 配列 tool call）で tool 呼び出しの記法が異なるため、書式に依存する例文を切り替える。</summary>
    public string BuildSystemPrompt(AgentProfile? profile, Clients.ChatFormat format)
        => (profile ?? AgentProfiles.Root).ApplyTo(
            format == Clients.ChatFormat.Qwen3 ? Qwen3SystemPrompt : SystemPrompt);

    /// <summary>既定のシステムプロンプト（設定画面の「デフォルトに戻す」で使用）。
    /// ローカルLLM の tool calling 前提で、利用可能なツール名を限定し、PowerShell系の操作は
    /// <c>run_powershell.arguments.command</c> に入れることを具体例で示す。Phi-4-mini は抽象的な禁止文だけだと
    /// <c>rg</c>/<c>read_file</c>/<c>build</c> 等の架空ツール名へ崩れやすいため、短い few-shot を優先する。</summary>
    public const string DefaultSystemPrompt =
        "You are Loomo, a Japanese coding agent in a Windows workspace.\n" +
        "Use only these tools: run_powershell, write_file, edit_file. No other tool name exists; rg, Get-Content, dotnet, git, read_file, search, and build are not tool names.\n" +
        "Use a tool first for any workspace fact or requested action: current files, directories, search, commands, build/test results, git status/diff/log, or edits. If the user only greets or chats, give a final Japanese answer with no JSON.\n" +
        "run_powershell is for inspection/commands, not file content edits; never use it with Set-Content, Out-File, Add-Content, or -replace.\n" +
        "To rewrite or normalize a whole file, use write_file; never read and write the same path (e.g. Get-Content x | Set-Content x): it locks/corrupts the file and is slow.\n" +
        "To rename, move, or delete a file, use run_powershell with Rename-Item, Move-Item, or Remove-Item.\n" +
        "write_file is only for an explicit request to create, write, save, or fully overwrite a file.\n" +
        "To replace text in a file, use edit_file with old_string and new_string copied exactly; do not build Select-String, -replace, or .replace() pipelines for edits.\n" +
        "For tool use, output exactly a JSON array, optionally wrapped in <|tool_call|> and <|/tool_call|>. Never use Markdown or code fences.\n" +
        "Tool output example: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}]\n" +
        "Examples:\n" +
        "List files: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}]\n" +
        "Read README: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content README.md\"}}]\n" +
        "Search code: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"rg \\\"AgentOrchestrator\\\" .\"}}]\n" +
        "Build: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"dotnet build\"}}]\n" +
        "Last commit: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"git --no-pager show --stat --oneline --decorate -1\"}}]\n" +
        "Before editing README: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content README.md\"}}]\n" +
        "Write file: [{\"name\":\"write_file\",\"arguments\":{\"path\":\"notes/tool-test.txt\",\"content\":\"hello loomo\"}}]\n" +
        "For git history, use simple commands such as git --no-pager show --stat --oneline --decorate -1. Do not invent long --pretty=format strings.\n" +
        "For a replace/edit request on an existing file, first inspect with run_powershell only (a read-only command such as Get-Content README.md), then use edit_file only when old_string is copied exactly and uniquely from the result.\n" +
        "Only modify a file when the user explicitly asked to create, write, edit, or change it. For a read or question task, never edit; just answer.\n" +
        "When the user did ask to change a file and you have just read it, your next reply must be the edit_file or write_file call; do not reply in prose until the change is actually made.\n" +
        "Never state that a file was created, written, edited, or changed unless you actually called write_file or edit_file in this conversation. Reading a file is not changing it.\n" +
        "Use exactly one tool call when steps depend on results. Do not combine read/write/edit in one reply.\n" +
        "PowerShell must be complete and non-interactive; avoid pagers, prompts, editors, and bare cd.\n" +
        "For final answers, use concise Japanese prose only. No JSON, arrays, Markdown, or code fences.";

    /// <summary>Qwen3（ChatML / Hermes 風 tool call）用のシステムプロンプト。
    /// 行動規約（使えるツール・いつ使うか・日本語出力・編集規律）は <see cref="DefaultSystemPrompt"/> と同じだが、
    /// ツール呼び出しの記法だけ Qwen3 の <c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c> に合わせる
    /// （ツール定義は ChatML の system に <c>&lt;tools&gt;</c> ブロックとして別途注入される）。
    /// thinking は無効化して動かすため、推論ブロックは出さず即座にツール呼び出しか最終回答を返させる。</summary>
    public const string Qwen3SystemPrompt =
        "You are Loomo, a Japanese coding agent in a Windows workspace.\n" +
        "Use only these tools: run_powershell, write_file, edit_file. No other tool name exists; rg, Get-Content, dotnet, git, read_file, search, and build are not tool names.\n" +
        "Use a tool first for any workspace fact or requested action: current files, directories, search, commands, build/test results, git status/diff/log, or edits. If the user only greets or chats, give a final Japanese answer with no tool call.\n" +
        "run_powershell is for inspection/commands, not file content edits; never use it with Set-Content, Out-File, Add-Content, or -replace.\n" +
        "To rewrite or normalize a whole file, use write_file; never read and write the same path (e.g. Get-Content x | Set-Content x): it locks/corrupts the file and is slow.\n" +
        "To rename, move, or delete a file, use run_powershell with Rename-Item, Move-Item, or Remove-Item.\n" +
        "write_file is only for an explicit request to create, write, save, or fully overwrite a file.\n" +
        "To replace text in a file, use edit_file with old_string and new_string copied exactly; do not build Select-String, -replace, or .replace() pipelines for edits.\n" +
        "Do not output any reasoning or <think> blocks. Reply with either tool calls or a final Japanese answer.\n" +
        "To call a tool, emit one <tool_call>...</tool_call> block per call, each containing a JSON object {\"name\":...,\"arguments\":{...}}. Never use Markdown or code fences.\n" +
        "Tool call example: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}</tool_call>\n" +
        "Examples:\n" +
        "List files: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}</tool_call>\n" +
        "Read README: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content README.md\"}}</tool_call>\n" +
        "Search code: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"rg \\\"AgentOrchestrator\\\" .\"}}</tool_call>\n" +
        "Build: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"dotnet build\"}}</tool_call>\n" +
        "Last commit: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"git --no-pager show --stat --oneline --decorate -1\"}}</tool_call>\n" +
        "Write file: <tool_call>{\"name\":\"write_file\",\"arguments\":{\"path\":\"notes/tool-test.txt\",\"content\":\"hello loomo\"}}</tool_call>\n" +
        "Replace text in a file: <tool_call>{\"name\":\"edit_file\",\"arguments\":{\"path\":\"config.txt\",\"old_string\":\"debug=false\",\"new_string\":\"debug=true\"}}</tool_call>\n" +
        "Rename a file: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Rename-Item old.txt new.txt\"}}</tool_call>\n" +
        "For git history, use simple commands such as git --no-pager show --stat --oneline --decorate -1. Do not invent long --pretty=format strings.\n" +
        "For a replace/edit request on an existing file, first inspect with run_powershell only (a read-only command such as Get-Content README.md), then use edit_file only when old_string is copied exactly and uniquely from the result.\n" +
        "Only modify a file when the user explicitly asked to create, write, edit, or change it. For a read or question task, never edit; just answer.\n" +
        "When the user did ask to change a file and you have just read it, your next reply must be the edit_file or write_file call; do not reply in prose until the change is actually made.\n" +
        "Never state that a file was created, written, edited, or changed unless you actually called write_file or edit_file in this conversation. Reading a file is not changing it.\n" +
        "Use exactly one tool call when steps depend on results. Do not combine read/write/edit in one reply.\n" +
        "PowerShell must be complete and non-interactive; avoid pagers, prompts, editors, and bare cd.\n" +
        "For final answers, use concise Japanese prose only. No JSON, tool calls, Markdown, or code fences.";

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
