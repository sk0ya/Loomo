#if LOOMO_EDITOR_MENU_LABELS
using Editor.Controls;
#endif

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタの右クリックメニューのネイティブ項目を日本語にする表。
/// <para>ライブラリ（sk0ya.Editor.Controls）は単体でも使われるので既定は英語のまま。Loomo は
/// UI 文言を日本語で揃えているため、ここで差し替えないと 1 つのメニューの中で
/// 「Copy Line」と「AIへ送る」が隣り合う。キー表記（<c>yy</c> / <c>gd</c> など）は Vim の綴りなので
/// 訳さない——ライブラリ側も見出しだけを外に出している。</para>
/// <para><b>見出しは定数で持つ</b>。Loomo はこの表で名付けたネイティブ項目のうち何本かを
/// メニューから外し、何本かを自前の項目へ差し替える（<c>ShellWindow.EditorNativeMenu</c>）——
/// その差し替えは「この見出しの項目」を目印に行うので、綴りの正本が 2 箇所にあると必ずズレる。</para></summary>
internal static class EditorMenuLabels
{
    internal const string CopySelection = "選択範囲をコピー";
    internal const string CopyLine = "行をコピー";
    internal const string CutSelection = "選択範囲を切り取り";
    internal const string CutLine = "行を切り取り";
    internal const string Paste = "貼り付け";
    internal const string Undo = "元に戻す";
    internal const string Redo = "やり直す";
    internal const string SelectAll = "すべて選択";
    internal const string Navigate = "移動";
    internal const string GoToDefinition = "定義へ移動";
    internal const string GoToImplementation = "実装へ移動";
    internal const string GoToTypeDefinition = "型定義へ移動";
    internal const string GoToDeclaration = "宣言へ移動";
    internal const string FindReferences = "参照を検索";
    internal const string RenameSymbol = "名前を変更…";
    internal const string CodeActions = "この位置で使える修正";
    internal const string FixAllInFile = "ファイル全体をまとめて修正";
    internal const string HoverInfo = "この位置の説明を表示";
    internal const string FormatDocument = "ファイル全体を整形";
    internal const string FormatSelection = "選択範囲を整形";

#if LOOMO_EDITOR_MENU_LABELS
    internal static EditorContextMenuLabels Japanese { get; } = new()
    {
        CopySelection = CopySelection,
        CopyLine = CopyLine,
        CutSelection = CutSelection,
        CutLine = CutLine,
        Paste = Paste,
        Undo = Undo,
        Redo = Redo,
        SelectAll = SelectAll,
        Navigate = Navigate,
        GoToDefinition = GoToDefinition,
        GoToImplementation = GoToImplementation,
        GoToTypeDefinition = GoToTypeDefinition,
        GoToDeclaration = GoToDeclaration,
        FindReferences = FindReferences,
        RenameSymbol = RenameSymbol,
        CodeActions = CodeActions,
        FixAllInFile = FixAllInFile,
        HoverInfo = HoverInfo,
        FormatDocument = FormatDocument,
        FormatSelection = FormatSelection,
    };
#endif
}
