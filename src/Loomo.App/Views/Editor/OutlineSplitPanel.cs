using System;
using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コード構造ビューの縦の割り付け：1 番目の子（アウトラインのツリー）の真下に 2 番目の子（②呼び出し解析）を置く。
/// 両方が収まるうちは StackPanel と同じく詰めて並べ、収まらなくなったら高さを分け合ってそれぞれの中で
/// スクロールさせる（<see cref="Split"/>）。
/// </summary>
/// <remarks>
/// 以前は外側の ScrollViewer＋StackPanel に両方を積んでいたため、ツリーは無限の高さで測られて全項目の
/// コンテナを作っていた（7500 行の .cs で約 570 項目）。外部の UI オートメーションのリスナーが居ると WPF は
/// レイアウトのたびにオートメーションのツリーを丸ごと辿るので、その全項目ぶんが打鍵ごとに約 3ms かかっていた
/// （§31.20）。ツリーに有限の高さを渡して UI 仮想化を効かせるのがこのパネルの役目。
/// </remarks>
public sealed class OutlineSplitPanel : Panel
{
    /// <summary>足りないときに②へ必ず残す高さの割合（ツリーが長くても②が潰れないように）。</summary>
    public const double BottomMinShare = 0.4;

    private double _topHeight;

    /// <summary>
    /// 使える高さ <paramref name="available"/> を上下に分ける。収まれば望みどおり、収まらなければ下は
    /// 「上の残り」と「全体の <see cref="BottomMinShare"/>」の大きい方を上限にし、上は残り全部を取る。
    /// </summary>
    public static (double Top, double Bottom) Split(double available, double topDesired, double bottomDesired)
    {
        if (double.IsInfinity(available) || topDesired + bottomDesired <= available)
            return (topDesired, bottomDesired);
        var bottom = Math.Min(bottomDesired, Math.Max(available * BottomMinShare, available - topDesired));
        return (Math.Max(0, available - bottom), bottom);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var top = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        var bottom = InternalChildren.Count > 1 ? InternalChildren[1] : null;

        top?.Measure(availableSize);
        bottom?.Measure(availableSize);
        var (topHeight, bottomHeight) = Split(
            availableSize.Height, top?.DesiredSize.Height ?? 0, bottom?.DesiredSize.Height ?? 0);

        // 割り当てが望みより小さい側だけ測り直す（中の ScrollViewer がその高さでスクロールを組む）。
        if (top is not null && topHeight < top.DesiredSize.Height)
            top.Measure(new Size(availableSize.Width, topHeight));
        if (bottom is not null && bottomHeight < bottom.DesiredSize.Height)
            bottom.Measure(new Size(availableSize.Width, bottomHeight));

        _topHeight = topHeight;
        var width = Math.Max(top?.DesiredSize.Width ?? 0, bottom?.DesiredSize.Width ?? 0);
        return new Size(width, topHeight + bottomHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var topHeight = Math.Min(_topHeight, finalSize.Height);
        if (InternalChildren.Count > 0)
            InternalChildren[0].Arrange(new Rect(0, 0, finalSize.Width, topHeight));
        if (InternalChildren.Count > 1)
            InternalChildren[1].Arrange(new Rect(0, topHeight, finalSize.Width, Math.Max(0, finalSize.Height - topHeight)));
        return finalSize;
    }
}
