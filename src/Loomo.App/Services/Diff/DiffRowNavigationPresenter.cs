using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>差分の指定行へ移動し、現在行を示す。左右並びはエディタ2つなので、移動とキャレットはそちらへ任せる。</summary>
internal sealed class DiffRowNavigationPresenter
{
    private readonly RichTextBox _unified;
    private readonly Action<int> _scrollSideToRow;
    private readonly Func<bool> _isSideBySide;
    private readonly Func<bool> _isMarkdownActive;
    private readonly Action<int> _scrollMarkdownToChange;
    private readonly Action _flushBuild;
    private readonly Action _updateLayout;
    private readonly Func<ScrollViewer?> _unifiedScrollViewer;
    private readonly Func<int, int> _blockEnd;
    private readonly List<(Paragraph Paragraph, Brush? OriginalBar)> _marks = new();

    public DiffRowNavigationPresenter(
        RichTextBox unified,
        Action<int> scrollSideToRow,
        Func<bool> isSideBySide,
        Func<bool> isMarkdownActive,
        Action<int> scrollMarkdownToChange,
        Action flushBuild,
        Action updateLayout,
        Func<ScrollViewer?> unifiedScrollViewer,
        Func<int, int> blockEnd)
    {
        _unified = unified;
        _scrollSideToRow = scrollSideToRow;
        _isSideBySide = isSideBySide;
        _isMarkdownActive = isMarkdownActive;
        _scrollMarkdownToChange = scrollMarkdownToChange;
        _flushBuild = flushBuild;
        _updateLayout = updateLayout;
        _unifiedScrollViewer = unifiedScrollViewer;
        _blockEnd = blockEnd;
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
            _scrollSideToRow(index);   // 現在ブロックの枠は中央の帯と一緒に描く（DiffSideBlockPresenter）
        else
            MarkAndScroll(_unified, _unifiedScrollViewer(), index);
    }

    public void ClearMarks()
    {
        foreach (var (paragraph, original) in _marks)
            paragraph.BorderBrush = original;
        _marks.Clear();
    }

    private void MarkAndScroll(RichTextBox box, ScrollViewer? scrollViewer, int index)
    {
        if (DiffRowLineMapper.ParagraphAt(box.Document, index) is not { } paragraph)
            return;
        // ブロックの全行の左端の帯を琥珀色にする（左右並びの現在ブロックの枠と同じ色）。行の背景は塗り替えない——
        // 先頭行だけを塗っていた頃は、どこまでが今のブロックか分からず、その行の追加／削除の色も消えていた。
        var end = _blockEnd(index);
        Block? block = paragraph;
        for (var i = index; i <= end && block is Paragraph row; i++, block = block.NextBlock)
            Mark(row);
        if (scrollViewer is null)
            return;
        var rect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var target = scrollViewer.VerticalOffset + rect.Top - scrollViewer.ViewportHeight * 0.35;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private void Mark(Paragraph paragraph)
    {
        _marks.Add((paragraph, paragraph.BorderBrush));
        paragraph.BorderBrush = DiffSideBlockPresenter.CurrentFrame;
    }
}
