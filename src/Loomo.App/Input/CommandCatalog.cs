using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Input;

/// <summary>
/// 1 つの操作コマンドのメタデータ。実際の振る舞い（<see cref="System.Action"/>）は持たず、
/// 「どんなコマンドが存在し、既定でどのキーに割り当たるか」だけを表す。アクションは
/// ShellWindow が <see cref="Id"/> をキーに結線する（同じ Id をコマンドパレット・設定画面・
/// キーディスパッチが共有する＝VS Code 流の command + keybinding 統合モデル）。
/// </summary>
/// <param name="Id">安定した識別子（例: <c>pane.focus.left</c>）。設定ファイルの上書きキーにもなる。</param>
/// <param name="Category">小分類（日本語）。どの大分類に属するかは <see cref="CommandCatalog.Areas"/> が決める。</param>
/// <param name="Title">表示名。</param>
/// <param name="DefaultBinding">既定ジェスチャ（<see cref="KeySequence"/> 表記）。null は既定未割当。</param>
/// <param name="EntersMode">実行時に入るモーダル状態名（現状 <see cref="CommandCatalog.ResizeMode"/> のみ）。</param>
public sealed record CommandDescriptor(
    string Id,
    string Category,
    string Title,
    string? DefaultBinding,
    string? EntersMode = null)
{
    /// <summary>大分類（エディタ・ターミナルなど）。キーボード設定の左ナビの単位。</summary>
    public CommandArea Area => CommandCatalog.AreaOf(Category);
}

/// <summary>コマンドの大分類。キーボード設定は「どこで使うキーか」で探すので、まずここで分け、
/// 中を <see cref="Categories"/>（小分類）で区切る。並び順がそのまま表示順になる。</summary>
/// <param name="Name">表示名。</param>
/// <param name="Icon">左ナビの頭に付ける記号。</param>
/// <param name="Categories">この大分類に属する小分類（表示順）。</param>
public sealed record CommandArea(string Name, string Icon, IReadOnlyList<string> Categories);

/// <summary>
/// アプリの全コマンドのメタデータ一覧（唯一の真実）。新しいショートカットは、ここに 1 行追加し、
/// ShellWindow の id→アクション対応へ実体を結ぶだけで、パレット・設定・キーバインドへ自動的に載る。
/// </summary>
public static class CommandCatalog
{
    /// <summary>リサイズモード（連打で伸縮を続け、Esc/Enter で抜ける）の状態名。</summary>
    public const string ResizeMode = "resize";

    // ===== 小分類 =====
    private const string CatPane = "ペイン操作";
    private const string CatMode = "表示モード";
    private const string CatTab = "タブ";
    private const string CatSidebar = "サイドバー";
    private const string CatPalette = "パレット";
    private const string CatSearch = "検索";
    private const string CatEdit = "編集";
    private const string CatNavigate = "移動";
    private const string CatCodeNavigate = "定義・参照";
    private const string CatProblems = "問題";
    private const string CatTest = "テスト";
    private const string CatFormat = "整形・コードスタイル";
    private const string CatRefactor = "リファクタリング";
    private const string CatGenerate = "コード生成";
    private const string CatStructure = "構造編集";
    private const string CatHistory = "ローカル履歴";
    private const string CatPackages = "NuGet";
    private const string CatTerminal = "ターミナル";
    private const string CatClaude = "Claude Code";
    private const string CatTask = "タスク";
    private const string CatComposer = "コンポーザ";
    private const string CatTrail = "軌跡";
    private const string CatHosting = "PR・Issue（GitHub／Azure DevOps）";

    /// <summary>大分類と、それぞれに属する小分類（表示順）。どのコマンドの小分類も、ちょうど 1 つの
    /// 大分類に属する（テストで保証）。新しい小分類を足したら、ここにも載せる。</summary>
    public static IReadOnlyList<CommandArea> Areas { get; } = new CommandArea[]
    {
        new("画面・ペイン", "▦", new[] { CatPane, CatMode, CatTab, CatSidebar }),
        new("パレット・検索", "🔍", new[] { CatPalette, CatSearch }),
        new("エディタ", "📝", new[]
        {
            CatEdit, CatNavigate, CatCodeNavigate, CatProblems, CatTest,
            CatFormat, CatRefactor, CatGenerate, CatStructure, CatHistory, CatPackages,
        }),
        new("ターミナル", "🖥", new[] { CatTerminal, CatClaude, CatTask, CatComposer }),
        new("Git", "⑂", new[] { CatHosting }),
        new("軌跡", "🧭", new[] { CatTrail }),
    };

    /// <summary>載っていない小分類の受け皿（カタログの書き漏れでも一覧から消えないように）。</summary>
    private static readonly CommandArea OtherArea = new("その他", "…", Array.Empty<string>());

    /// <summary>小分類が属する大分類。</summary>
    public static CommandArea AreaOf(string category)
        => Areas.FirstOrDefault(area => area.Categories.Contains(category)) ?? OtherArea;

    /// <summary>C# 編集操作の分類を小分類へ写す。</summary>
    private static string CategoryOf(CSharpEditorCommandGroup group) => group switch
    {
        CSharpEditorCommandGroup.Navigation => CatCodeNavigate,
        CSharpEditorCommandGroup.Formatting => CatFormat,
        CSharpEditorCommandGroup.Generate => CatGenerate,
        CSharpEditorCommandGroup.Structure => CatStructure,
        _ => CatRefactor,
    };

    public static IReadOnlyList<CommandDescriptor> All { get; } = new CommandDescriptor[]
    {
        // ===== パレット =====
        // 開くキーは、パレットの中でもう一度押すと「検索対象を次へ」になる（pane.search と同じ流儀で、
        // 覚えるキーを1本に保つ）。対象を直に選ぶキー（palette.goTo*）とは別口。
        new CommandDescriptor("palette.open", CatPalette, "コマンドパレットを開く／検索対象を次へ切り替え", "Ctrl+Shift+P"),
        new CommandDescriptor("palette.openFromPrefix", CatPalette, "コマンドパレットを開く（プレフィックス）／検索対象を次へ切り替え", "Ctrl+W P"),
        // パレットの「探して飛ぶ」入口（§24.2）。開いた時点でモードのプレフィックスが入っているだけで、
        // 中身は同じ1つのパレット——だからここで開いてから Tab で探し方を変えられる。
        // ファイルだけ既定キーを与える（VS Code と同じ Ctrl+P。毎回使う動線なので）。他はパレットの
        // プレフィックス（# @ :）で届くため既定未割当にして、キーの取り合いを増やさない。
        new CommandDescriptor("palette.goToFile", CatPalette, "ファイルを検索して開く", "Ctrl+P"),
        new CommandDescriptor("palette.goToText", CatPalette, "テキストを検索して開く", null),
        new CommandDescriptor("palette.goToSymbol", CatPalette, "シンボルを検索して開く", null),
        new CommandDescriptor("palette.goToLine", CatPalette, "現在のファイルの行へ移動", null),
        // パレットを開いている間の「検索対象（探し方）の切替」。検索ペインの search.nextScope と同じ役割で、
        // 既定は Tab／Shift+Tab。パレットの外では実行可否（CanExecuteKeyboardCommand）が false になるので、
        // ふつうのフォーカス移動としての Tab は食われない。
        new CommandDescriptor("palette.nextScope", CatPalette, "検索対象を次へ切り替え（パレット）", "Tab"),
        new CommandDescriptor("palette.previousScope", CatPalette, "検索対象を前へ切り替え（パレット）", "Shift+Tab"),

        // ===== ペイン操作（vim 風 Ctrl+W プレフィックス） =====
        new CommandDescriptor("pane.focus.left", CatPane, "左のペインへフォーカス", "Ctrl+W H"),
        new CommandDescriptor("pane.focus.down", CatPane, "下のペインへフォーカス", "Ctrl+W J"),
        new CommandDescriptor("pane.focus.up", CatPane, "上のペインへフォーカス", "Ctrl+W K"),
        new CommandDescriptor("pane.focus.right", CatPane, "右のペインへフォーカス", "Ctrl+W L"),
        new CommandDescriptor("pane.resize.left", CatPane, "ペインを左へリサイズ", "Ctrl+W Shift+H", ResizeMode),
        new CommandDescriptor("pane.resize.down", CatPane, "ペインを下へリサイズ", "Ctrl+W Shift+J", ResizeMode),
        new CommandDescriptor("pane.resize.up", CatPane, "ペインを上へリサイズ", "Ctrl+W Shift+K", ResizeMode),
        new CommandDescriptor("pane.resize.right", CatPane, "ペインを右へリサイズ", "Ctrl+W Shift+L", ResizeMode),
        new CommandDescriptor("pane.zoom", CatPane, "ペインのズーム切替", "Ctrl+W Z"),
        new CommandDescriptor("pane.fullscreen", CatPane, "現在のペインを画面全体に表示／復元", "F11"),
        new CommandDescriptor("pane.close", CatPane, "ペイン／分割を閉じる", "Ctrl+W X"),
        new CommandDescriptor("pane.split.vertical", CatPane, "ペインを左右に分割", "Ctrl+W V"),
        new CommandDescriptor("pane.split.horizontal", CatPane, "ペインを上下に分割", "Ctrl+W S"),
        new CommandDescriptor("pane.split.closeView", CatPane, "分割ビューを畳む", "Ctrl+W Q"),
        // 検索だけは VS Code 流に既定キーを与える（全文検索の起点なので毎回使う）。
        new CommandDescriptor("pane.search", CatPane, "検索を開く", "Ctrl+Shift+F"),
        // ファイル一覧は既定キーを与えない（サイドバーのツリーと役割が違うだけで、
        // 毎回使う起点ではない。割り当てたい人は設定画面から付けられる）。
        new CommandDescriptor("pane.files", CatPane, "ファイル一覧を開く", null),
        // ハーネスも既定キーなし（毎回の起点ではない。§37）。
        new CommandDescriptor("pane.harness", CatPane, "ハーネスを開く", null),

        // ===== 検索 =====
        // pane.search は検索ペインの外では開く、検索ペイン内では次の対象へ切り替える。
        // そのため既定キーを二重に登録せず、同じ Ctrl+Shift+F を自然な動線として使える。
        new CommandDescriptor("search.nextScope", CatSearch, "次の検索対象へ切り替え", null),
        new CommandDescriptor("search.previousScope", CatSearch, "前の検索対象へ切り替え", null),
        new CommandDescriptor("search.scope.text", CatSearch, "検索対象をテキストにする", null),
        new CommandDescriptor("search.scope.fileName", CatSearch, "検索対象をファイル名にする", null),
        new CommandDescriptor("search.scope.terminal", CatSearch, "検索対象をターミナルにする", null),
        new CommandDescriptor("search.scope.class", CatSearch, "検索対象をクラスにする", null),
        new CommandDescriptor("search.scope.symbol", CatSearch, "検索対象をシンボルにする", null),

        // ===== ターミナル =====
        // 最近のコマンド（§24.20）。選ぶとプロンプトへ入力するだけで実行はしない（§24.3 の「送る」）。
        // 既定キーは与えない——パレットの一覧にも並ぶので、毎回の起点にしたい人だけが付ければよい。
        new CommandDescriptor("terminal.recentCommands", CatTerminal, "最近のコマンドを選び直す", null),
        // 既定のキーは割り当てない（Ctrl+Shift+T は mode.toggle が使っている）。設定から足せる（§24.41）。
        new CommandDescriptor("terminal.reopenClosed", CatTerminal, "閉じたターミナルを開き直す", null),
        new CommandDescriptor("terminal.compareOutput", CatTerminal, "コマンドの出力を前回と比較", null),
        new CommandDescriptor("terminal.openOutput", CatTerminal, "コマンドの出力をエディタで開く", null),
        // 出典付きでペグボードへ残す（§24.26）。右クリック（選択時）と同じ実装へ入る。
        new CommandDescriptor("terminal.pinOutput", CatTerminal, "コマンドの出力をペグボードへ残す", null),
        new CommandDescriptor("terminal.pinSelection", CatTerminal, "ターミナルの選択をペグボードへ残す", null),
        new CommandDescriptor("trail.bookmark", CatTrail, "いまの地点にしおりを付ける", null),
        new CommandDescriptor("trail.bookmarks", CatTrail, "しおり一覧", null),
        new CommandDescriptor("trail.daySummary", CatTrail, "この日のまとめ", null),
        new CommandDescriptor("trail.reshowAway", CatTrail, "留守中のまとめをもう一度表示", null),
        new CommandDescriptor("trail.toggleExpanded", CatTrail, "軌跡の展開ビューを開く／閉じる", null),

        // ===== タスク（tasks.json・§31.17） =====
        // 既定キーは与えない。VS Code の Ctrl+Shift+B は「型定義へ移動」とブラウザのブックマークバーが使っている。
        new CommandDescriptor("tasks.run", CatTask, "タスクを実行…", null),
        new CommandDescriptor("tasks.runDefaultBuild", CatTask, "既定のビルドタスクを実行", null),

        // ===== 問題 =====
        new CommandDescriptor("problems.next", CatProblems, "次の問題へ移動", "F8"),
        new CommandDescriptor("problems.previous", CatProblems, "前の問題へ移動", "Shift+F8"),
        // 表示中の問題の箇所を1枚の抜粋タブに並べる（§35.3）。既定キーは与えない——同じことが問題一覧の
        // 「▤ 抜粋タブ」と右クリックから届くので、毎回の起点にしたい人だけが付ければよい。
        // ===== ナビゲーション（Rider の Navigate Back/Forward・Last Edit Location・Recent Files・File Structure）=====
        // キーは Rider の既定に揃える。ただし Rider の Ctrl+E（最近のファイル）は Vim のスクロールと取り合うので
        // 採らず、Rider で「最近の場所」に当たる Ctrl+Shift+E を「最近のファイル」に充て、開いている最中に
        // もう一度押すと最近の場所へ切り替える（パレットを開くキーと同じ流儀で、覚えるキーを1本に保つ）。
        new CommandDescriptor("nav.back", CatNavigate, "前にいた場所へ戻る", "Ctrl+Alt+Left"),
        new CommandDescriptor("nav.forward", CatNavigate, "戻る前の場所へ進む", "Ctrl+Alt+Right"),
        new CommandDescriptor("nav.lastEditLocation", CatNavigate, "最後に編集した場所へ（続けて押すと遡る）", "Ctrl+Shift+Backspace"),
        new CommandDescriptor("nav.recentFiles", CatNavigate, "最近のファイル（もう一度押すと最近の場所）", "Ctrl+Shift+E"),
        new CommandDescriptor("nav.recentLocations", CatNavigate, "最近の場所", null),
        // Rider の Navigate to Test（Ctrl+Shift+T）は「表示モードの切替」が使っているので Ctrl+Alt+T に置く。
        new CommandDescriptor("nav.relatedTest", CatNavigate, "テスト⇔実装を行き来する", "Ctrl+Alt+T"),
        new CommandDescriptor("nav.fileStructure", CatNavigate, "ファイル構造（宣言の一覧）へ移動", "Ctrl+F12"),
        new CommandDescriptor("problems.openAsExcerpts", CatProblems, "表示中の問題を抜粋タブで開く", null),

        // ===== ローカル履歴（Rider の Local History・§36）=====
        // 既定キーは与えない（Rider も Local History に既定キーを持たない）。パレットから引く。
        new CommandDescriptor("localHistory.show", CatHistory, "ローカル履歴を表示（版 ↔ いまの差分）", null),
        new CommandDescriptor("localHistory.restore", CatHistory, "ローカル履歴から戻す…", null),
        new CommandDescriptor("localHistory.recent", CatHistory, "最近の変更（ローカル履歴・全ファイル）", null),
        new CommandDescriptor("localHistory.restoreDeleted", CatHistory, "削除したファイルを復元（ローカル履歴）", null),

        // ===== 文字コード（§23.3.4）=====
        // 既定キーは与えない（調べたいときにだけ引く）。パレットから。
        new CommandDescriptor("editor.inspectEncoding", CatEdit, "文字コードを調べる（BOM・改行・見えない文字・文字化け）", null),

        // ===== Claude Code（§24.1.1・§38）=====
        new CommandDescriptor("claude.agents", CatClaude, "動いている Claude Code の一覧（承認待ち・入力待ちを先に）", null),
        new CommandDescriptor("claude.resumeSession", CatClaude, "Claude Code のセッションを再開…", null),
        new CommandDescriptor("claude.installHooks", CatClaude, "Claude Code のフックを設定（承認待ち・セッションを Loomo へ知らせる）", null),
        new CommandDescriptor("claude.uninstallHooks", CatClaude, "Claude Code のフックを外す", null),

        // ===== PR・Issue（GitHub §24.27・Azure DevOps §24.27.1）=====
        new CommandDescriptor("github.pullRequests", CatHosting, "GitHub: プルリクエスト（読む・差分・ワークツリー・レビュー）", null),
        new CommandDescriptor("github.issues", CatHosting, "GitHub: Issue（読む・コメント）", null),
        new CommandDescriptor("azure.pullRequests", CatHosting, "Azure DevOps: プルリクエスト（読む・差分・ワークツリー）", null),

        // ===== NuGet（§36）=====
        new CommandDescriptor("nuget.manage", CatPackages, "NuGet パッケージを管理…", null),

        // ===== エディタ（意味的な選択・§24.9） =====
        // 既定キーは VS Code 準拠。Ctrl+W 系は<b>使わない</b>——Loomo ではペイン操作の vim 風
        // プレフィックスで、部屋そのものの動線（JetBrains の Ctrl+W に引きずられない）。
        new CommandDescriptor("editor.save", CatEdit, "現在のファイルを保存", "Ctrl+S"),
        new CommandDescriptor("editor.selection.expand", CatEdit, "選択を意味的に広げる", "Shift+Alt+Right"),
        new CommandDescriptor("editor.selection.shrink", CatEdit, "選択を1段戻す", "Shift+Alt+Left"),
        // ガターの ▶ はマウス専用なので、キーボードからの実行経路をコマンドとして持たせる（§28.10）。
        // 既定キーは与えない——テストを書く人だけが使う操作で、既定の1打鍵を占有する理由が無い。
        new CommandDescriptor("editor.test.runAtCaret", CatTest, "カーソル行のテストを実行", null),
        new CommandDescriptor("editor.test.debugAtCaret", CatTest, "カーソル行のテストをデバッグ", null),
        // .http（§24.30）。REST Client の Ctrl+Alt+R は既定に取らない——.http を書く人だけの操作で、
        // 既定の1打鍵を全員から占有する理由が無い（テストの実行と同じ扱い。割り当てはキーボード設定で）。
        new CommandDescriptor("http.sendAtCaret", CatEdit, "HTTP: カーソル位置のリクエストを送る", null),
        // Emmet（§36.7）。既定キーは VS Code に無いので与えない（Tab は snippet・Vim と取り合う）。
        new CommandDescriptor("editor.emmet.expand", CatEdit, "Emmet: 略記を展開", null),
        new CommandDescriptor("editor.emmet.wrap", CatEdit, "Emmet: 選択を略記で囲む…", null),

        // ===== 表示モード（ソロ／レイアウト） =====
        new CommandDescriptor("stage.cycle", CatMode, "次へ切り替え（ソロ＝舞台／レイアウト＝保存レイアウト）", "Ctrl+T"),
        new CommandDescriptor("mode.toggle", CatMode, "表示モードを切り替え（集中→分割→ドック）", "Ctrl+Shift+T"),

        // ===== コンポーザ =====
        new CommandDescriptor("composer.run", CatComposer, "本文をターミナルで実行", "Ctrl+Enter"),

        // ===== サイドバー（既定未割当。設定画面でキーを与えられる） =====
        new CommandDescriptor("sidebar.explorer", CatSidebar, "エクスプローラを開く", null),
        new CommandDescriptor("sidebar.tabs", CatSidebar, "タブセクションを開閉", null),
        new CommandDescriptor("sidebar.sessions", CatSidebar, "AIセッションを開く", null),
        new CommandDescriptor("sidebar.git", CatSidebar, "Git を開く", null),
        new CommandDescriptor("sidebar.pegboard", CatSidebar, "ペグボードを開く", null),
        new CommandDescriptor("sidebar.solution", CatSidebar, "ソリューション（C#）を開く", null),
        new CommandDescriptor("sidebar.settings", CatSidebar, "設定を開く", null),
        new CommandDescriptor("sidebar.appearance", CatSidebar, "外観（テーマ）を開く", null),
        new CommandDescriptor("explorer.revealActiveFile", CatSidebar, "エクスプローラで現在のファイルを選択（同期）", null),

        // ===== タブ（既定未割当） =====
        new CommandDescriptor("tab.newTerminal", CatTab, "新しいターミナルタブ", null),
        new CommandDescriptor("tab.newEditor", CatTab, "新しいエディタタブ", null),
        new CommandDescriptor("tab.newBrowser", CatTab, "新しいブラウザタブ", null),
    }
    .Concat(CSharpEditorCommandCatalog.All.Select(command =>
        new CommandDescriptor(command.Id, CategoryOf(command.Group), command.Title, command.DefaultBinding)))
    .ToArray();

    private static readonly Dictionary<string, CommandDescriptor> ById =
        All.ToDictionary(c => c.Id);

    public static CommandDescriptor? Find(string id) =>
        ById.TryGetValue(id, out var d) ? d : null;
}
