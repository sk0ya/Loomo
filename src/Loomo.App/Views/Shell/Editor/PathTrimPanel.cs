using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>「フォルダー部分」「ファイル名」の2つを左詰めで横に並べ、幅が足りないときは<b>先頭の子だけ</b>を
/// 縮めるパネル（Editor ペインのヘッダーのパス表示用）。末尾省略の TextBlock 1つだと肝心のファイル名から
/// 消え、Grid の * 列だと短いパスでもフォルダー部分が残り幅いっぱいに広がってファイル名との間が空く。</summary>
public sealed class PathTrimPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count < 2)
            return MeasureSingle(availableSize);

        var head = InternalChildren[0];
        var tail = InternalChildren[1];
        tail.Measure(new Size(availableSize.Width, availableSize.Height));
        var rest = Math.Max(0, availableSize.Width - tail.DesiredSize.Width);
        head.Measure(new Size(rest, availableSize.Height));
        return new Size(
            head.DesiredSize.Width + tail.DesiredSize.Width,
            Math.Max(head.DesiredSize.Height, tail.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren)
                child.Arrange(new Rect(finalSize));
            return finalSize;
        }

        var head = InternalChildren[0];
        var tail = InternalChildren[1];
        var tailWidth = Math.Min(tail.DesiredSize.Width, finalSize.Width);
        var headWidth = Math.Min(head.DesiredSize.Width, finalSize.Width - tailWidth);
        head.Arrange(new Rect(0, 0, headWidth, finalSize.Height));
        tail.Arrange(new Rect(headWidth, 0, tailWidth, finalSize.Height));
        return finalSize;
    }

    private Size MeasureSingle(Size availableSize)
    {
        var size = new Size();
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(availableSize);
            size = child.DesiredSize;
        }
        return size;
    }
}
