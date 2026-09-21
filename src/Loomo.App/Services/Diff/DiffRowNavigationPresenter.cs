using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>差分の指定行へ移動し、左右ペインとガターに現在行を示す。</summary>
internal sealed class DiffRowNavigationPresenter
{
    private static readonly Brush CurrentMark = DiffFlowDocumentRenderer.FrozenBrush("#66FFC107");
    private readonly RichTextBox _unified;
    private readonly RichTextBox _left;
    private readonly RichTextBox _right;
    private readonly RichTextBox _leftGutter;
    private readonly RichTextBox _rightGutter;
    private readonly Func<bool> _isSideBySide;
    private readonly Func<bool> _isMarkdownActive;
    private readonly Action<int> _scrollMarkdownToChange;
    private readonly Action _flushBuild;
    private readonly Action _updateLayout;
    private readonly Func<ScrollViewer?> _unifiedScrollViewer;
    private readonly Func<ScrollViewer?> _leftTextScrollViewer;
    private readonly List<(Paragraph Paragraph, Brush? Original)> _marks = new();

    public DiffRowNavigationPresenter(
        RichTextBox unified,
        RichTextBox left,
        RichTextBox right,
        RichTextBox leftGutter,
        RichTextBox rightGutter,
        Func<bool> isSideBySide,
        Func<bool> isMarkdownActive,
        Action<int> scrollMarkdownToChange,
        Action flushBuild,
        Action updateLayout,
        Func<ScrollViewer?> unifiedScrollViewer,
        Func<ScrollViewer?> leftTextScrollViewer)
    {
        _unified = unified;
        _left = left;
        _right = right;
        _leftGutter = leftGutter;
        _rightGutter = rightGutter;
        _isSideBySide = isSideBySide;
        _isMarkdownActive = isMarkdownActive;
        _scrollMarkdownToChange = scrollMarkdownToChange;
        _flushBuild = flushBuild;
        _updateLayout = updateLayout;
        _unifiedScrollViewer = unifiedScrollViewer;
        _leftTextScrollViewer = leftTextScrollViewer;
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
        {
            MarkAndScroll(_left, _leftTextScrollViewer(), index);
            MarkOnly(_right, index);
            MarkOnly(_leftGutter, index);
            MarkOnly(_rightGutter, index);
        }
        else
        {
            MarkAndScroll(_unified, _unifiedScrollViewer(), index);
        }
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

    private void MarkOnly(RichTextBox box, int index)
    {
        if (DiffRowLineMapper.ParagraphAt(box.Document, index) is { } paragraph)
            Mark(paragraph);
    }

    private void Mark(Paragraph paragraph)
    {
        _marks.Add((paragraph, paragraph.Background));
        paragraph.Background = CurrentMark;
    }
}
