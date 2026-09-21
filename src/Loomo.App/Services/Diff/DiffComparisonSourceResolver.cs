using sk0ya.Loomo.Core.Files;
using Editor.Controls;

namespace sk0ya.Loomo.App.Services;

internal sealed record DiffComparisonResolution(DiffComparison? Comparison, string? ErrorMessage);

/// <summary>ファイルやクリップボードからDiff比較の左右の素材を組み立てる。</summary>
internal static class DiffComparisonSourceResolver
{
    /// <summary>選択メニューの比較先を、エディタ状態とクリップボードから比較データへ解決する。</summary>
    public static DiffComparisonResolution FromSelection(
        SelectionComparePresentation presentation,
        string selectedText,
        VimEditorControl? editor,
        string filePath,
        Func<string?> readClipboard)
        => presentation.Kind switch
        {
            SelectionCompareKind.SelectionWithClipboard => WithClipboard(
                presentation.LeftTitle, selectedText, filePath, readClipboard),
            SelectionCompareKind.EditorWithClipboard when editor is not null => WithClipboard(
                presentation.LeftTitle, editor.Text, filePath, readClipboard),
            SelectionCompareKind.SavedEditorWithBuffer when editor is not null => FromSavedFile(
                filePath, presentation.LeftTitle, editor.Text),
            _ => new(null, null),
        };

    public static DiffComparisonResolution FromFiles(
        string leftPath, string? rightPath, Func<string?> readClipboard)
    {
        try
        {
            if (BinaryFileDetector.IsBinary(leftPath)
                || (rightPath is { } binaryCheck && BinaryFileDetector.IsBinary(binaryCheck)))
                return new(null, "バイナリファイルは比較できません。");

            var leftName = Path.GetFileName(leftPath);
            var leftText = File.ReadAllText(leftPath);
            if (rightPath is { Length: > 0 })
                return new(new DiffComparison(
                    leftName, leftText, Path.GetFileName(rightPath), File.ReadAllText(rightPath), rightPath), null);

            if (readClipboard() is not { } clipboard)
                return new(null, "クリップボードにテキストがありません。");
            return new(new DiffComparison(
                leftName, leftText, "クリップボード", clipboard, leftPath, FileIsLeft: true), null);
        }
        catch (Exception ex)
        {
            return new(null, $"ファイルを読めませんでした: {ex.Message}");
        }
    }

    public static DiffComparisonResolution FromSavedFile(string path, string title, string bufferText)
    {
        try
        {
            return new(new DiffComparison(
                $"{title}（保存済み）", File.ReadAllText(path),
                $"{title}（編集中）", bufferText, path), null);
        }
        catch (Exception ex)
        {
            return new(null, $"保存済みの内容を読めませんでした: {ex.Message}");
        }
    }

    public static DiffComparisonResolution WithClipboard(
        string leftTitle, string leftText, string filePath, Func<string?> readClipboard)
    {
        if (readClipboard() is not { } clipboard)
            return new(null, "クリップボードにテキストがありません。");
        return new(new DiffComparison(
            leftTitle, leftText, "クリップボード", clipboard, filePath, FileIsLeft: true), null);
    }
}
