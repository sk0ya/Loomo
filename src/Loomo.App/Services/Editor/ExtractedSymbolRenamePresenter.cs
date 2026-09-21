using System.Windows;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>抽出リファクタリングの適用前に識別子を入力し、編集範囲の生成名を置き換える。</summary>
internal static class ExtractedSymbolRenamePresenter
{
    public static (IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes, bool Cancelled) Rename(
        Window owner,
        RefactoringItem item,
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes)
    {
        if (item.Group != RefactoringGroup.Extract ||
            ExtractedSymbolName.Find(changes) is not { Length: > 0 } generated)
            return (changes, false);

        while (true)
        {
            var chosen = InputDialog.Prompt(
                owner, "リファクタリング", $"「{item.Title}」— 新しい名前", generated);
            if (chosen is null)
                return (changes, true);
            if (ExtractedSymbolName.IsValidIdentifier(chosen))
                return (ExtractedSymbolName.Rename(changes, generated, chosen), false);

            MessageBox.Show(owner, "識別子として使えない名前です。", "リファクタリング",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
