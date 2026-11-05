using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>ブランチ一覧の1行。子は「現在印・名前・右側の付記（左→右の並び順）」の順に置く。
/// 幅が足りないときは<b>名前を優先</b>し、右側の付記を<b>右端（＝末尾の子）から順に</b>落とす。
/// DockPanel の右寄せだと付記が先に幅を取り、「3日前」が残って肝心のブランチ名が省略されていた。
/// 付記を全部落としてもまだ足りないときだけ、名前を省略する（名前の TextBlock の TextTrimming に任せる）。</summary>
public sealed class BranchRowPanel : Panel
{
    private const int LeadIndex = 0;
    private const int NameIndex = 1;
    private const int FirstTrailingIndex = 2;

    /// <summary>名前の省略なしの幅（Measure で無制限に測った値）。</summary>
    private double _nameFullWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (UIElement child in InternalChildren)
            child.Measure(infinite);

        if (InternalChildren.Count <= NameIndex)
            return SumDesired();

        _nameFullWidth = InternalChildren[NameIndex].DesiredSize.Width;
        var layout = Plan(availableSize.Width);
        InternalChildren[NameIndex].Measure(new Size(layout.NameWidth, availableSize.Height));

        var height = 0.0;
        foreach (UIElement child in InternalChildren)
            height = Math.Max(height, child.DesiredSize.Height);
        return new Size(double.IsInfinity(availableSize.Width) ? layout.UsedWidth : Math.Min(layout.UsedWidth, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count <= NameIndex)
        {
            var x0 = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(x0, 0, child.DesiredSize.Width, finalSize.Height));
                x0 += child.DesiredSize.Width;
            }
            return finalSize;
        }

        var layout = Plan(finalSize.Width);
        var lead = InternalChildren[LeadIndex];
        lead.Arrange(new Rect(0, 0, lead.DesiredSize.Width, finalSize.Height));
        var name = InternalChildren[NameIndex];
        // 測ったときと幅が違えば、その幅で測り直してから置く（省略記号の位置を合わせる）。
        if (!name.DesiredSize.Width.Equals(layout.NameWidth))
            name.Measure(new Size(layout.NameWidth, finalSize.Height));
        name.Arrange(new Rect(lead.DesiredSize.Width, 0, layout.NameWidth, finalSize.Height));

        // 残す付記は右端に詰めて並べる（行をまたいで列に見えるように）。落とす分は幅 0 で置いて見せない。
        var x = finalSize.Width - layout.TrailingWidth;
        for (var i = FirstTrailingIndex; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (i < layout.TrailingEnd)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width;
            }
            else
            {
                child.Arrange(new Rect(finalSize.Width, 0, 0, 0));
            }
        }
        return finalSize;
    }

    /// <summary>名前の欲しい幅を先に確保し、残りに収まるところまで付記を左から採る。</summary>
    private RowLayout Plan(double width)
    {
        var leadWidth = InternalChildren[LeadIndex].DesiredSize.Width;
        var nameDesired = _nameFullWidth;

        var end = InternalChildren.Count;
        var trailing = 0.0;
        for (var i = FirstTrailingIndex; i < end; i++)
            trailing += InternalChildren[i].DesiredSize.Width;

        if (!double.IsInfinity(width))
        {
            while (end > FirstTrailingIndex && leadWidth + nameDesired + trailing > width)
            {
                end--;
                trailing -= InternalChildren[end].DesiredSize.Width;
            }
        }

        var nameWidth = double.IsInfinity(width)
            ? nameDesired
            : Math.Max(0, Math.Min(nameDesired, width - leadWidth - trailing));
        return new RowLayout(nameWidth, trailing, end, leadWidth + nameWidth + trailing);
    }

    private Size SumDesired()
    {
        var w = 0.0;
        var h = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            w += child.DesiredSize.Width;
            h = Math.Max(h, child.DesiredSize.Height);
        }
        return new Size(w, h);
    }

    private readonly record struct RowLayout(double NameWidth, double TrailingWidth, int TrailingEnd, double UsedWidth);
}
