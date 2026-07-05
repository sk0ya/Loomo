using System.Collections.Generic;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Ai;

/// <summary>AIプロバイダ設定。appsettings / ユーザー設定からバインドする。</summary>
public sealed class AiSettings
{
    public const string DefaultLocalModel = "qwen3-4b-q4_k_m";

    /// <summary>現在選択中のプロバイダ。</summary>
    public AiProvider Provider { get; set; } = AiProvider.Local;

    /// <summary>UIのカラーテーマ（配色）。既定はダーク。</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>AIウォームアップを有効にするか。既定は有効。
    /// 有効なら起動時／ワークスペース確定時に現在のローカルモデルをロードし、system プロンプト＋ツール定義の
    /// 安定プレフィックスを常駐エンジンへ prefill して KV キャッシュを温める（初回ターンの prefill を
    /// 払い直さず体感が速くなる）。暖機の実行中は AI への指示を受け付けない
    /// （ローカル推論エンジンがモデルロード・prefill 中で占有されるため）。
    /// 無効にすると暖機を一切行わず、最初のAIターンで通常どおりロード／prefill する。</summary>
    public bool WarmupEnabled { get; set; } = true;

    /// <summary>アクセントカラーの上書き（"#RRGGBB" 等）。null/空ならテーマ既定のアクセントを使う。</summary>
    public string? AccentColor { get; set; }

    /// <summary>ウィンドウ最下部の軌跡（操作ログ）バーを表示するか。既定は表示。OFF にすると記録が
    /// あってもバーごと隠す（記録自体は続くので、再表示すればそれまでの軌跡も見える）。バーの
    /// コンテキストメニュー「軌跡を非表示にする」や設定の「外観」トグルからここへ書き戻される。</summary>
    public bool TrailVisible { get; set; } = true;

    /// <summary>コマンド実行・書込の安全設計（設計書 §10）。</summary>
    public SafetySettings Safety { get; set; } = new();

    /// <summary>AI操作トレース（観測性・設計書 §20）の設定。</summary>
    public ObservabilitySettings Observability { get; set; } = new();

    /// <summary>埋め込み Vim エディタの設定。</summary>
    public VimSettings Vim { get; set; } = new();

    /// <summary>キーボードショートカットのユーザー上書き（既定と異なるものだけ保持）。</summary>
    public KeybindingSettings Keybindings { get; set; } = new();

    /// <summary>エディタ／Markdownプレビュー／ターミナルの配色・フォント設定。
    /// アプリUIの配色（<see cref="Theme"/>/<see cref="AccentColor"/>）とは独立に各コンポーネントへ適用する。</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>言語サーバー（LSP）まわりの Loomo 側設定。拡張子→サーバーの対応そのものはエディタ側
    /// （<c>LspServerRegistry</c>・%APPDATA%/Loomo/lsp-servers.json）が持ち、ここには「促しバーを今後出さない
    /// 拡張子」など Loomo の UI 状態だけを置く。</summary>
    public LspSettings Lsp { get; set; } = new();

    /// <summary>ローカルLLM（in-process／CPU）。既定は llama.cpp バックエンドの Qwen3-4B GGUF Q4_K_M
    /// （decode は ONNX と互角・prefill とロードは速い・モデル入手容易）。バックエンドは modelPath で
    /// 振り分かる（<see cref="Clients.LocalInferenceRouter"/>：<c>.gguf</c>→llama.cpp／フォルダ→ONNX）。</summary>
    public ProviderConfig Local { get; set; } = new()
    {
        Model = DefaultLocalModel,
        MaxTokens = 1024
    };

    public string SystemPrompt => DefaultSystemPrompt;

    /// <summary>チャット（対話）ターンでユーザー発話の直前に差し込む追加プロンプト。共有システムプロンプトは
    /// モード中立に保ち、対話固有の出力規約（簡潔な日本語の文章で答える）はここに置く。warmup 済みの
    /// system プレフィックスより後ろ（user ターン）へ入るため KV 共有を壊さない。英語指示・日本語出力の方針に揃える。</summary>
    public const string ChatTurnPreamble =
        "[Interactive chat] Reply to the user in concise Japanese prose. " +
        "Use a tool only when you need workspace data or must change a file; for a greeting or general question, answer directly.";

    /// <summary>ワークフローの単発ステップで指示文の直前に差し込む追加プロンプト。各ステップは自己完結した
    /// 単発タスク（要約・翻訳・整形など、処理対象は指示文に含まれる）なので、指示が求める出力形式・言語を
    /// そのまま守らせる（共有システムプロンプトの汎用規約を、その単発タスク向けに具体化する）。</summary>
    public const string WorkflowTurnPreamble =
        "[Single task] The instruction below is self-contained and already includes the content to process. " +
        "Produce exactly what the instruction asks for, following its requested language and format " +
        "(it may ask for English, a bullet list, a Markdown table, or code). " +
        "The output language the instruction requests overrides the default Japanese: " +
        "if it asks to translate the content into English, write the entire answer in English with no Japanese; " +
        "if it asks for English text, output English only. " +
        "Use a tool only if the instruction needs workspace data or a file change; otherwise answer directly. " +
        "Output only the result, with no extra preamble or explanation unless asked.";

    public string BuildSystemPrompt(AgentProfile? profile = null)
        => (profile ?? AgentProfiles.Root).ApplyTo(SystemPrompt);

    /// <summary>チャット記法に合わせたシステムプロンプトを組み立てる。Qwen3（ChatML/Hermes tool call）と
    /// Phi-4（JSON 配列 tool call）で tool 呼び出しの記法が異なるため、書式に依存する例文を切り替える。</summary>
    public string BuildSystemPrompt(AgentProfile? profile, Clients.ChatFormat format)
        => (profile ?? AgentProfiles.Root).ApplyTo(
            format == Clients.ChatFormat.Qwen3 ? Qwen3SystemPrompt : SystemPrompt);

    /// <summary>既定のシステムプロンプト（設定画面の「デフォルトに戻す」で使用）。
    /// Phi-4-mini 用。架空ツール名への崩れを抑えるため、短い few-shot とファイル編集規律だけを残す。</summary>
    public const string DefaultSystemPrompt =
        "You are Loomo, a Windows coding agent. Default final answers are concise Japanese; obey requested language/format.\n" +
        "Tools only: run_powershell, write_file, edit_file, web_search. rg/Get-Content/dotnet/git/read_file/search/build are commands or files, not tools.\n" +
        "Use tools for workspace facts/actions; web_search only for web facts; answer chat directly.\n" +
        "run_powershell inspects/runs complete non-interactive commands and may Rename/Move/Remove files, but must not edit file content: no Set-Content/Out-File/Add-Content/-replace/>/same-file pipe. Read before editing existing files; then use edit_file with exact old_string or write_file for create/full overwrite. Never claim changes unless the tool succeeded.\n" +
        "Tool calls: JSON array, optionally wrapped in <|tool_call|>...<|/tool_call|>; no Markdown fences.\n" +
        "Examples:\n" +
        "List files: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}]\n" +
        "Read file: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content README.md\"}}]\n" +
        "Search code: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"rg \\\"needle\\\" .\"}}]\n" +
        "Build: [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"dotnet build\"}}]\n" +
        "Edit file: [{\"name\":\"edit_file\",\"arguments\":{\"path\":\"notes.txt\",\"old_string\":\"old\",\"new_string\":\"new\"}}]\n" +
        "Search web: [{\"name\":\"web_search\",\"arguments\":{\"query\":\".NET release notes\"}}]\n" +
        "Final answers must not contain tool-call JSON or markers.";

    /// <summary>Qwen3（ChatML / Hermes 風 tool call）用のシステムプロンプト。
    /// ツール呼び出しの記法は Qwen3 の <c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c>
    /// （ツール定義は ChatML の system に <c>&lt;tools&gt;</c> ブロックとして別途注入される）。
    /// thinking は無効化して動かすため、推論ブロックは出さず即座にツール呼び出しか最終回答を返させる。
    ///
    /// 2026-06 全面改訂：旧版は能力ハーネスの失敗タスクを個別に潰す形で文言が共進化し、ハーネスの
    /// シードファイル名（README.md 等）が few-shot 例に混入していた＝評価セットへの過適合。
    /// 本版は (1) ハーネスと語彙・ファイル名・操作が被らない例に差し替え、(2) 個別タスクの対症文を
    /// 一般原則（事実はツール結果からのみ／エラーを成功と報告しない／複数部の完遂と検証／
    /// old_string の厳密複写）へ昇格させ、(3) 実測で load-bearing と分かっている構造
    /// （許可ツール名の列挙・「意図→呼び出し」のラベル付き例・独立した1文ルール）は維持する。
    /// ハーネス固有名の再混入は Qwen3PromptFormatterTests の回帰テストで機械的に防ぐ。</summary>
    public const string Qwen3SystemPrompt =
        "You are Loomo, a Windows coding agent. Default final answers are concise Japanese; obey requested language/format.\n" +
        "Tools only: run_powershell, write_file, edit_file, web_search. rg/Get-Content/dotnet/git/read_file/search/build are commands or files, not tools.\n" +
        "Use tools for workspace facts/actions; web_search only for web facts; answer chat directly. Change files only when asked.\n" +
        "run_powershell: one complete non-interactive PowerShell command for inspect/list/search/count/build/test/git and Rename/Move/Remove/Copy/New-Item. Do not edit file content with it: no Set-Content/Add-Content/Out-File/>/-replace/same-file pipe.\n" +
        "write_file: create/full overwrite. edit_file: exact old_string -> new_string after reading the file. Check errors; never report failed/skipped work as done. No reasoning or <think> blocks.\n" +
        "Tool call format: one <tool_call>{\"name\":...,\"arguments\":{...}}</tool_call> block per call; no Markdown fences.\n" +
        "Examples:\n" +
        "List files: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}</tool_call>\n" +
        "Read file: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content src/server.js\"}}</tool_call>\n" +
        "Search text: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"rg \\\"onError\\\" src\"}}</tool_call>\n" +
        "Build: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"dotnet build\"}}</tool_call>\n" +
        "Rename file: <tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Rename-Item drafts/letter.txt final-letter.txt\"}}</tool_call>\n" +
        "Edit file: <tool_call>{\"name\":\"edit_file\",\"arguments\":{\"path\":\"project.toml\",\"old_string\":\"timeout = 30\",\"new_string\":\"timeout = 60\"}}</tool_call>\n" +
        "Search web: <tool_call>{\"name\":\"web_search\",\"arguments\":{\"query\":\".NET release notes\"}}</tool_call>\n" +
        "Final answers must contain no <tool_call> blocks.";

    public ProviderConfig ConfigFor(AiProvider provider) => Local;
}

public sealed class ProviderConfig
{
    public string Model { get; set; } = "";

    /// <summary>ローカル推論エンジンが読むモデルパス。GGUF なら <c>*.gguf</c> ファイル、ONNX Runtime GenAI
    /// なら <c>genai_config.json</c> ＋ <c>*.onnx</c> ＋ tokenizer 一式を含むフォルダ。空なら未設定。</summary>
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

/// <summary>言語サーバー（LSP）に関する Loomo 側の UI 設定。拡張子→サーバーの対応・カスタムサーバーは
/// エディタの <c>LspServerRegistry</c> が永続化するため、ここは持たない。ファイルを開いたときの
/// 「インストールを促すバー」を今後出さない拡張子の一覧だけを保持する。</summary>
public sealed class LspSettings
{
    /// <summary>促しバーで「今後表示しない」を選んだ拡張子（先頭ドット付き・小文字）。</summary>
    public List<string> DismissedPromptExtensions { get; set; } = new();
}

public sealed class VimSettings
{
    /// <summary>
    /// 埋め込みエディタで Vim キーバインドを有効にする。
    /// </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>キーボードショートカットのユーザー上書き。
/// キーはコマンド Id（例 <c>pane.focus.left</c>）、値はジェスチャ表記（例 <c>Ctrl+W H</c>）。
/// 既定（カタログ）と同じものは保持せず、変更したものだけを持つ。値を空文字にすると「未割当」を表す
/// （既定でキーが付くコマンドのバインドを意図的に外した状態）。ジェスチャ表記の解釈は UI 層（App）が担う
/// ため、ここでは文字列のまま保持し WPF へ依存しない。</summary>
public sealed class KeybindingSettings
{
    public Dictionary<string, string> Overrides { get; set; } = new();
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

    /// <summary>
    /// marp プレビューを発表モード（スライドを1枚ずつ表示・ページ送り）にするか。OFF（既定）は全スライドを
    /// 縦並びでスクロール表示する。効くのはフロントマターに <c>marp: true</c> がある文書のみで、非 marp の
    /// 通常 Markdown はこの設定に関わらず常にドキュメント表示になる。
    /// </summary>
    public bool MarkdownSlideMode { get; set; }

    /// <summary>ターミナルの配色テーマ（背景/文字色のプリセット）。<c>Dark / Light / Dracula / Nord / SolarizedDark</c>。</summary>
    public string TerminalTheme { get; set; } = "Dark";

    /// <summary>ターミナルのフォントファミリ。null/空ならコントロール既定。</summary>
    public string? TerminalFontFamily { get; set; }

    /// <summary>ターミナルのフォントサイズ。0 以下ならコントロール既定。</summary>
    public double TerminalFontSize { get; set; }

    /// <summary>ターミナルで OpenType のプログラミングフォント合字（<c>=&gt;</c> / <c>!=</c> / <c>-&gt;</c> 等）を
    /// 有効にするか。既定 OFF。フォントが合字を持つ場合のみ描画に反映される。</summary>
    public bool TerminalFontLigatures { get; set; }
}
