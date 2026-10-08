using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Views;

/// <summary>構文木を単位にした編集（§35.2）のWPF入口。対象の決定と編集の計算は
/// <see cref="CSharpStructuralEditing"/>（純関数）が持ち、ここは本文を渡して結果を1回のundoで適用するだけ。</summary>
public partial class ShellWindow
{
    /// <summary>構造編集のコマンドなら実行して true。<see cref="ExecuteCSharpEditorCommand"/> から呼ぶ。</summary>
    private bool TryRunStructuralEdit(string id, VimEditorControl control)
    {
        Func<string, LspRange?, LspPosition, StructuralEditResult>? compute = id switch
        {
            CSharpEditorCommandCatalog.MoveStatementUp => (text, selection, caret) =>
                CSharpStructuralEditing.Move(text, selection, caret, StructuralListKind.Lines, forward: false),
            CSharpEditorCommandCatalog.MoveStatementDown => (text, selection, caret) =>
                CSharpStructuralEditing.Move(text, selection, caret, StructuralListKind.Lines, forward: true),
            CSharpEditorCommandCatalog.MoveElementLeft => (text, selection, caret) =>
                CSharpStructuralEditing.Move(text, selection, caret, StructuralListKind.Separated, forward: false),
            CSharpEditorCommandCatalog.MoveElementRight => (text, selection, caret) =>
                CSharpStructuralEditing.Move(text, selection, caret, StructuralListKind.Separated, forward: true),
            CSharpEditorCommandCatalog.DeleteSyntaxNode => CSharpStructuralEditing.Delete,
            _ => null,
        };
        if (compute is null) return false;

        var caret = new LspPosition(control.Caret.Line, control.Caret.Column);
        var result = compute(control.Text, LiveSelection(control, caret), caret);
        if (result.Edits is not { } edits || result.Selection is not { } moved)
        {
            ShowRefactorStatus(result.Error ?? "構文要素を編集できませんでした。");
            return true;
        }

        // 同期で計算して同期で適用するので、間に本文が動くことはない（版の照合は不要）。
        if (!control.TryApplyLspTextEdits(edits, expectedVersion: null, out var error))
        {
            ShowRefactorStatus($"構文要素を編集できませんでした: {error}");
            return true;
        }

        // 動かした要素を選択したままにして、続けて押せば同じ要素がさらに動くようにする。
        if (moved.Start == moved.End)
            control.NavigateTo(moved.Start.Line, moved.Start.Character);
        else
            control.SelectRange(moved.Start.Line, moved.Start.Character, moved.End.Line, moved.End.Character);
        ShowRefactorStatus(result.Summary ?? "");
        return true;
    }

    /// <summary>対象に使ってよい選択。<c>SelectRange</c> で付けた選択は Normal モードのまま残り、
    /// 別の場所をクリックしても消えない（キャレットだけが動く）。キャレットが選択の外（両端を含めて）に
    /// 出ていれば利用者が自分で動かしたとみなして選択を捨て、キャレット位置で掴む。端ちょうどで比べないのは、
    /// エディタが選択を「終端を含む」形で持つので、利用者が作った選択ではキャレットが LSP の終端と1桁ずれうるため。
    /// 実機で、動かした文の選択が残ったまま引数をクリックして左右移動すると「動かせる要素が無い」に
    /// なっていた。</summary>
    private static LspRange? LiveSelection(VimEditorControl control, LspPosition caret)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection) return null;
        return Compare(selection.Start, caret) <= 0 && Compare(caret, selection.End) <= 0 ? selection : null;

        static int Compare(LspPosition a, LspPosition b)
            => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);
    }
}
