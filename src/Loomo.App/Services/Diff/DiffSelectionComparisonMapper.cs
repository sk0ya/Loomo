using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal sealed record DiffSelectionComparisonResult(
    DiffComparison? Comparison,
    string? ErrorMessage);

/// <summary>選択テキストとクリップボードの内容を比較データへ変換する。</summary>
internal static class DiffSelectionComparisonMapper
{
    internal static DiffSelectionComparisonResult FromClipboard(string selectedText, string? clipboardText)
    {
        if (string.IsNullOrEmpty(selectedText))
            return new(null, "差分本体でテキストを選択してから実行してください。");
        if (clipboardText is null)
            return new(null, "クリップボードにテキストがありません。");

        // 選択範囲はファイル先頭を表すものではないため、行番号用の元ファイル情報を持たせない。
        return new(new DiffComparison("差分の選択範囲", selectedText, "クリップボード", clipboardText), null);
    }
}
