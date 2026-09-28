using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>差分の指定行へ移動し、現在行を示す。左右並びはエディタ2つなので、移動とキャレットはそちらへ任せる。</summary>
internal sealed class DiffRowNavigationPresenter
{
    private static readonly Brush CurrentMark = DiffFlowDocumentRenderer.FrozenBrush("#66FFC107");
    private readonly RichTextBox _unified;
    private readonly Action<int> _scrollSideToRow;
    private readonly Func<bool> _isSideBySide;
    private readonly Func<bool> _isMarkdownActive;
    private readonly Action<int> _scrollMarkdownToChange;
    private readonly Action _flushBuild;
    private readonly Action _updateLayout;
    private readonly Func<ScrollViewer?> _unifiedScrollViewer;
    private readonly List<(Paragraph Paragraph, Brush? Original)> _marks = new();

    public DiffRowNavigationPresenter(
        RichTextBox unified,
        Action<int> scrollSideToRow,
        Func<bool> isSideBySide,
        Func<bool> isMarkdownActive,
        Action<int> scrollMarkdownToChange,
        Action flushBuild,
        Action updateLayout,
        Func<ScrollViewer?> unifiedScrollViewer)
    {
        _unified = unified;
        _scrollSideToRow = scrollSideToRow;
        _isSideBySide = isSideBySide;
        _isMarkdownActive = isMarkdownActive;
        _scrollMarkdownToChange = scrollMarkdownToChange;
        _flushBuild = flushBuild;
        _updateLayout = updateLayout;
        _unifiedScrollViewer = unifiedScrollViewer;
    }

    public void ScrollToRow(int index)
    {
        if (_isMarkdownActive())
        {
            // Markdown表示では行番号ではなくページ内の変更グループ番号を使う。
            _scrollMarkdownToChange(index);
            return;
        }

        _flushBuild();
        _updateLayout();
        ClearMarks();
        if (_isSideBySide())
            _scrollSideToRow(index);   // 現在行はキャレットの行として見える
        else
            MarkAndScroll(_unified, _unifiedScrollViewer(), index);
    }

    public void ClearMarks()
    {
        foreach (var (paragraph, original) in _marks)
            paragraph.Background = original;
        _marks.Clear();
    }

    private void MarkAndScroll(RichTextBox box, ScrollViewer? scrollViewer, int index)
    {
        if (DiffRowLineMapper.ParagraphAt(box.Document, index) is not { } paragraph)
            return;
        Mark(paragraph);
        if (scrollViewer is null)
            return;
        var rect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var target = scrollViewer.VerticalOffset + rect.Top - scrollViewer.ViewportHeight * 0.35;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private void Mark(Paragraph paragraph)
    {
        _marks.Add((paragraph, paragraph.Background));
        paragraph.Background = CurrentMark;
    }
}
