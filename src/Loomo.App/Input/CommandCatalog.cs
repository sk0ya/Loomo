using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.App.Input;

/// <summary>
/// 1 つの操作コマンドのメタデータ。実際の振る舞い（<see cref="System.Action"/>）は持たず、
/// 「どんなコマンドが存在し、既定でどのキーに割り当たるか」だけを表す。アクションは
/// ShellWindow が <see cref="Id"/> をキーに結線する（同じ Id をコマンドパレット・設定画面・
/// キーディスパッチが共有する＝VS Code 流の command + keybinding 統合モデル）。
/// </summary>
/// <param name="Id">安定した識別子（例: <c>pane.focus.left</c>）。設定ファイルの上書きキーにもなる。</param>
/// <param name="Category">設定画面・パレットでのグルーピング名（日本語）。</param>
/// <param name="Title">表示名。</param>
/// <param name="DefaultBinding">既定ジェスチャ（<see cref="KeySequence"/> 表記）。null は既定未割当。</param>
/// <param name="EntersMode">実行時に入るモーダル状態名（現状 <see cref="CommandCatalog.ResizeMode"/> のみ）。</param>
public sealed record CommandDescriptor(
    string Id,
    string Category,
    string Title,
    string? DefaultBinding,
    string? EntersMode = null);

/// <summary>
/// アプリの全コマンドのメタデータ一覧（唯一の真実）。新しいショートカットは、ここに 1 行追加し、
/// ShellWindow の id→アクション対応へ実体を結ぶだけで、パレット・設定・キーバインドへ自動的に載る。
/// </summary>
public static class CommandCatalog
{
    /// <summary>リサイズモード（連打で伸縮を続け、Esc/Enter で抜ける）の状態名。</summary>
    public const string ResizeMode = "resize";

    private const string CatPane = "ペイン操作";
    private const string CatPalette = "パレット";
    private const string CatComposer = "コンポーザ";
    private const string CatSidebar = "サイドバー";
    private const string CatTab = "タブ";
    private const string CatStage = "セッション";

    public static IReadOnlyList<CommandDescriptor> All { get; } = new[]
    {
        // ===== パレット =====
        new CommandDescriptor("palette.open", CatPalette, "コマンドパレットを開く", "Ctrl+Shift+P"),
        new CommandDescriptor("palette.openFromPrefix", CatPalette, "コマンドパレットを開く（プレフィックス）", "Ctrl+W P"),

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

        // ===== セッション（ソロ／レイアウト） =====
        new CommandDescriptor("stage.cycle", CatStage, "次へ切り替え（ソロ＝舞台／レイアウト＝保存レイアウト）", "Ctrl+T"),
        new CommandDescriptor("mode.toggle", CatStage, "ソロ⇄レイアウトを切り替え", "Ctrl+Shift+T"),

        // ===== コンポーザ =====
        new CommandDescriptor("composer.run", CatComposer, "本文をターミナルで実行", "Ctrl+Enter"),

        // ===== サイドバー（既定未割当。設定画面でキーを与えられる） =====
        new CommandDescriptor("sidebar.explorer", CatSidebar, "エクスプローラを開く", null),
        // 検索だけは VS Code 流に既定キーを与える（全文検索の起点なので毎回使う）。
        new CommandDescriptor("sidebar.search", CatSidebar, "検索を開く", "Ctrl+Shift+F"),
        new CommandDescriptor("sidebar.tabs", CatSidebar, "タブ一覧を開く", null),
        new CommandDescriptor("sidebar.sessions", CatSidebar, "AIセッションを開く", null),
        new CommandDescriptor("sidebar.git", CatSidebar, "Git を開く", null),
        new CommandDescriptor("sidebar.pegboard", CatSidebar, "ペグボードを開く", null),
        new CommandDescriptor("sidebar.settings", CatSidebar, "設定を開く", null),
        new CommandDescriptor("sidebar.appearance", CatSidebar, "外観（テーマ）を開く", null),

        // ===== タブ（既定未割当） =====
        new CommandDescriptor("tab.newTerminal", CatTab, "新しいターミナルタブ", null),
        new CommandDescriptor("tab.newEditor", CatTab, "新しいエディタタブ", null),
        new CommandDescriptor("tab.newBrowser", CatTab, "新しいブラウザタブ", null),
    };

    private static readonly Dictionary<string, CommandDescriptor> ById =
        All.ToDictionary(c => c.Id);

    public static CommandDescriptor? Find(string id) =>
        ById.TryGetValue(id, out var d) ? d : null;
}
