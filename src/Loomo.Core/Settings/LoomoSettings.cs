using System.Collections.Generic;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Core.Settings;

/// <summary>Loomo 全体のユーザー設定。settings.json から読み書きする。</summary>
public sealed class LoomoSettings
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

    /// <summary>「機能をペインに前面表示する」ときの配置の振る舞い（「AIへ送る」「ブラウザへ送る」
    /// 「差分を開く」等の共通経路）。既定は <see cref="PaneOpenBehavior.Main"/>（左上と入れ替え＝従来動作）。</summary>
    public PaneOpenBehavior PaneOpenBehavior { get; set; } = PaneOpenBehavior.Main;

    /// <summary>サブペインをメインのどちら側に置くか（横に並べる＝右／縦に並べる＝下）。
    /// <see cref="PaneOpenBehavior.Sub"/>・<see cref="PaneOpenBehavior.Loop"/> の配置先に効く。
    /// 既定は <see cref="PaneSubDirection.Horizontal"/>（右＝従来動作）。</summary>
    public PaneSubDirection PaneSubDirection { get; set; } = PaneSubDirection.Horizontal;

    /// <summary>ウィンドウ最下部の軌跡（操作ログ）バーを表示するか。既定は表示。OFF にすると記録が
    /// あってもバーごと隠す（記録自体は続くので、再表示すればそれまでの軌跡も見える）。バーの
    /// コンテキストメニュー「軌跡を非表示にする」や設定の「外観」トグルからここへ書き戻される。</summary>
    public bool TrailVisible { get; set; } = true;

    /// <summary>ブラウザペインのブックマークバー（アドレス欄の下の帯）を表示するか。既定は表示。
    /// バーの右クリック・ブックマーク一覧の切替ボタン・Ctrl+Shift+B からここへ書き戻され、次回起動でも保たれる。</summary>
    public bool BrowserBookmarkBarVisible { get; set; } = true;

    /// <summary>Git ペインの下段「コミット詳細」（選択コミットの <c>git show --stat</c>）を表示するか。
    /// 既定は表示。Git ペインのタイトル領域のトグルからここへ書き戻され、次回起動でも保たれる。</summary>
    public bool GitCommitDetailVisible { get; set; } = true;

    /// <summary>Git ペインの左列「ブランチ一覧」（タグ／リモート／サブモジュールを含む縦列）を表示するか。
    /// 既定は表示。コミット一覧の見出し「コミット」の左の開閉ボタンからここへ書き戻され、次回起動でも保たれる。</summary>
    public bool GitBranchColumnVisible { get; set; } = true;

    /// <summary>Git ペイン左列の下段で選んでいる参照の種類（"Tags" / "Remotes" / "Submodules"）。
    /// 3つを縦に積むとブランチ一覧が潰れるので切替式にしてあり、その選択を次回起動へ持ち越す。
    /// 文字列で持つのは、列挙が増減しても古い settings.json が読めなくならないようにするため
    /// （読めない値は既定＝タグに落とす）。</summary>
    public string GitReferenceTab { get; set; } = "Tags";

    /// <summary>ActivityBar（左端の縦帯）の項目配置。上段バーと中段バーのどちらに何を置くかを持つ。</summary>
    public ActivityBarSettings ActivityBar { get; set; } = new();

    /// <summary>サイドバーの TABS（タブ一覧）に出す種別。見出しクリックで開く設定ビューから書き戻される。</summary>
    public TabsPanelSettings TabsPanel { get; set; } = new();

    /// <summary>コマンド実行・書込の安全設計（設計書 §10）。</summary>
    public SafetySettings Safety { get; set; } = new();

    /// <summary>AI操作トレース（観測性・設計書 §20）の設定。</summary>
    public ObservabilitySettings Observability { get; set; } = new();

    /// <summary>埋め込み Vim エディタの設定。</summary>
    public VimSettings Vim { get; set; } = new();

    /// <summary>埋め込みエディタの表示設定。</summary>
    public EditorSettings Editor { get; set; } = new();

    /// <summary>入力の先読み（キャレットの先に薄く出る提案）のうち、ローカル LLM が作る方の設定。</summary>
    public InlineCompletionSettings InlineCompletion { get; set; } = new();

    /// <summary>キーボードショートカットのユーザー上書き（既定と異なるものだけ保持）。</summary>
    public KeybindingSettings Keybindings { get; set; } = new();

    /// <summary>エディタ／Markdownプレビュー／ターミナルの配色・フォント設定。
    /// アプリUIの配色（<see cref="Theme"/>/<see cref="AccentColor"/>）とは独立に各コンポーネントへ適用する。</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>言語サーバー（LSP）まわりの Loomo 側設定。拡張子→サーバーの対応そのものは
    /// <c>LspServerTable</c>（%APPDATA%/Loomo/lsp-servers.json）が持ち、ここには「促しバーを今後出さない
    /// 拡張子」など Loomo の UI 状態だけを置く。</summary>
    public LspSettings Lsp { get; set; } = new();

    /// <summary>ローカルLLM（in-process／CPU）。既定は llama.cpp バックエンドの Qwen3-4B GGUF Q4_K_M
    /// （decode は ONNX と互角・prefill とロードは速い・モデル入手容易）。バックエンドは modelPath で
    /// 振り分かる（<see cref="Clients.LocalInferenceRouter"/>：<c>.gguf</c>→llama.cpp／フォルダ→ONNX）。</summary>
    public ProviderConfig Local { get; set; } = new()
    {
        Model = DefaultLocalModel,
        MaxTokens = 4096
    };

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

    /// <summary>
    /// ローカル推論エンジンのコンテキスト窓の上書き。
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

/// <summary>言語サーバー（LSP）に関する Loomo 側の UI 設定。拡張子→サーバーの対応・カスタムサーバーは
/// <c>LspServerTable</c> が別ファイルへ永続化するため、ここは持たない。ファイルを開いたときの
/// 「インストールを促すバー」を今後出さない拡張子の一覧だけを保持する。</summary>
public sealed class LspSettings
{
    /// <summary>促しバーで「今後表示しない」を選んだ拡張子（先頭ドット付き・小文字）。</summary>
    public List<string> DismissedPromptExtensions { get; set; } = new();
}

/// <summary>ActivityBar（左端の縦帯）の項目配置。Loomo の ActivityBar は上段（画面の上から）と
/// 中段（画面の中ほどから）の2本に分かれ、それぞれが自分のサイドバー区画を持つ。ここには
/// どちらのバーに何をどの順で置くかだけを、項目 Id（"explorer" 等）の並びとして保存する。
/// 空なら既定配置（上段＝エクスプローラ他／中段＝タブ一覧）を使う。未知の Id は読み捨て、
/// どちらにも現れなかった項目は既定の配置先の末尾へ落とす（アプリ更新で項目が増えても消えない）。</summary>
public sealed class ActivityBarSettings
{
    /// <summary>上段バーの項目 Id（上から順）。</summary>
    public List<string> Primary { get; set; } = new();

    /// <summary>中段バーの項目 Id（上から順）。</summary>
    public List<string> Secondary { get; set; } = new();
}

/// <summary>サイドバーの TABS（タブ一覧）に出す種別。タブそのものは種別ごとに混ぜずに並べてあるので、
/// 「いまはエディタのタブだけ見たい」を種別単位で選べる。隠しても閉じるわけではなく、一覧に出さないだけ
/// （ペインのタブ列にはそのまま残る）。既定は3種とも表示。</summary>
public sealed class TabsPanelSettings
{
    /// <summary>エディタのタブを一覧に出すか。</summary>
    public bool ShowEditor { get; set; } = true;

    /// <summary>ブラウザのタブを一覧に出すか。</summary>
    public bool ShowBrowser { get; set; } = true;

    /// <summary>ブラウザのタブをURLのホスト名ごとにまとめるか。既定は従来どおりまとめない。</summary>
    public bool GroupBrowserByDomain { get; set; }

    /// <summary>ターミナルのタブを一覧に出すか。</summary>
    public bool ShowTerminal { get; set; } = true;
}

public sealed class VimSettings
{
    /// <summary>
    /// 埋め込みエディタで Vim キーバインドを有効にする。
    /// </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>埋め込みエディタの表示に関する設定（Vim キーバインドの有無とは独立）。
/// ここに並ぶ真偽値は <c>Editor.Core.Config.VimOptions</c> の項目を Loomo 設定画面から
/// 触れるようにしたもので、適用は <c>VimEditorControl.ExecuteCommand("set ...")</c>（Vim の
/// <c>:set</c> 相当）を経由する（設定画面のチェックボックスを ON/OFF する体験になる）。既定値は
/// ライブラリ既定と一致させてあり、未設定ユーザーの見た目は変わらない。</summary>
public sealed class EditorSettings
{
    /// <summary>全角スペース・行末スペースのハイライト表示（sk0ya.Editor.Controls 1.0.43）。
    /// エディタ既定と同じく既定は有効。</summary>
    public bool HighlightWhitespace { get; set; } = true;

    /// <summary>行番号ガターの表示（Vim <c>number</c>）。既定 ON。</summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>相対行番号表示（Vim <c>relativenumber</c>）。Vim キーバインド利用時に
    /// ジャンプ数の目安になる。既定 OFF。</summary>
    public bool RelativeLineNumbers { get; set; }

    /// <summary>カーソル行の背景ハイライト（Vim <c>cursorline</c>）。既定 ON。</summary>
    public bool HighlightCurrentLine { get; set; } = true;

    /// <summary>長い行の折り返し表示（Vim <c>wrap</c>）。OFF（既定）では横スクロール。</summary>
    public bool WordWrap { get; set; }

    /// <summary>コード全体を縮小表示するミニマップ（Vim <c>minimap</c>）。既定 OFF。</summary>
    public bool ShowMinimap { get; set; }

    /// <summary>インデントの深さを示す縦線（Vim <c>indentguides</c>）。既定 OFF。</summary>
    public bool ShowIndentGuides { get; set; }

    /// <summary>C# ファイルを開いたとき、先頭の <c>using</c> 節を閉じて表示する。
    /// 既定 OFF。クラスやメソッドなど、ほかの折りたたみ範囲には干渉しない。</summary>
    public bool CollapseUsingsOnOpen { get; set; }

    /// <summary>C# ファイルの保存時に、.editorconfigに沿った整形とusing整理を行う。既定 OFF。</summary>
    public bool CleanCSharpOnSave { get; set; }

    /// <summary>括弧・引用符を入力したとき対応する閉じ記号を自動挿入する（Vim <c>pairs</c>）。既定 OFF。</summary>
    public bool AutoClosePairs { get; set; }

    /// <summary>入力の先読み（キャレットの先に薄く出る提案）を使うか。既定は有効。
    /// これを切ると、同じバッファの既出行からの予測もローカル LLM の先読みもまとめて止まる。</summary>
    public bool InlineSuggest { get; set; } = true;

    /// <summary>LSPが返すparameter name等のinlay hintをエディタ内へ表示する。既定 OFF。</summary>
    public bool ShowInlayHints { get; set; }

    /// <summary>インデント幅（Vim <c>tabstop</c>/<c>shiftwidth</c>）。既定 2（ライブラリ既定と同じ）。</summary>
    public int TabWidth { get; set; } = 2;

    /// <summary>Tab入力・自動インデントでスペースを使うか（false = タブ文字、Vim <c>expandtab</c>）。既定 ON。</summary>
    public bool UseSpacesForTab { get; set; } = true;

    /// <summary>Markdownへ画像を貼り付けたときの保存先ディレクトリ（Markdownファイル自身のディレクトリからの
    /// 相対パス）。{filename}/{date}/{time}/{datetime} プレースホルダ対応。既定は <c>images</c>
    /// （<c>Editor.Core.Editing.ImagePasteOptions</c> のライブラリ既定と同じ、sk0ya.Editor.Controls 1.0.45）。</summary>
    public string ImagePasteDirectory { get; set; } = "images";

    /// <summary>Markdownへ画像を貼り付けたときの保存ファイル名（拡張子込み）。
    /// {filename}/{date}/{time}/{datetime}/{seq} プレースホルダ対応。既定は <c>{filename}-{datetime}.png</c>。</summary>
    public string ImagePasteFileName { get; set; } = "{filename}-{datetime}.png";

    /// <summary>貼り付け画像の Markdown リンクに入れる代替テキスト（<c>![alt](path)</c> の alt）。
    /// 空なら保存ファイル名（拡張子なし）を使う。</summary>
    public string ImagePasteAltText { get; set; } = "";
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

    /// <summary>
    /// Markdown プレビューに<b>アウトライン（見出し一覧）</b>を出すか。既定 OFF。ページ右端に固定表示し、
    /// 項目クリックでその見出しへ飛ぶ。見出しが無い文書では ON でも出さない（幅を空けない）。
    /// marp スライド表示（<see cref="MarkdownSlideMode"/> の対象文書）には効かない。
    /// </summary>
    public bool MarkdownOutlineVisible { get; set; }

    /// <summary>ターミナルの配色テーマ（背景/文字色のプリセット）。<c>Dark / Light / Dracula / Nord / SolarizedDark</c>。</summary>
    public string TerminalTheme { get; set; } = "Dark";

    /// <summary>ターミナルのフォントファミリ。null/空ならコントロール既定。</summary>
    public string? TerminalFontFamily { get; set; }

    /// <summary>ターミナルのフォントサイズ。0 以下ならコントロール既定。</summary>
    public double TerminalFontSize { get; set; }

    /// <summary>ターミナルで OpenType のプログラミングフォント合字（<c>=&gt;</c> / <c>!=</c> / <c>-&gt;</c> 等）を
    /// 有効にするか。既定 OFF。フォントが合字を持つ場合のみ描画に反映される。</summary>
    public bool TerminalFontLigatures { get; set; }

    /// <summary>袖（右端のミニチュア一覧）を何列で並べるか。1（既定）または 2。2列にすると同じ袖幅で
    /// 倍の枚数が視界に入る代わりにカード1枚は小さくなる。範囲外の値は UI 側で 1〜2 に丸める。</summary>
    public int WingColumns { get; set; } = 1;

    /// <summary>アプリ UI 全体の基準フォントサイズ（本文の px）。0 以下なら未設定（既定サイズを使う）。
    /// サイドバー・設定・ツリー・タブ・AIバーなど WPF で組んだ UI に一律に効き、見出し／補足などの大小関係は
    /// 比率を保って連動する。エディタ／ターミナルのフォントサイズ（<see cref="EditorFontSize"/> /
    /// <see cref="TerminalFontSize"/>）とは独立で連動しない。</summary>
    public double UiFontSize { get; set; }
}
