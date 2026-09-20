using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>各セルの余白を GridViewRowPresenter 既定の左右6pxではなく「右だけ <see cref="ColumnGap"/>」に
/// する行プレゼンター。列幅を中身の実寸へ詰める（<see cref="GitSessionView.LogTailColumnWidth"/>）と、
/// 既定の左右余白では文字が入りきらず末尾省略になり、余白ゼロにすると今度は隣の列の文字と
/// 字送りゼロで接する。<b>右にだけ</b>空けることで、左端は列境界にそろえたまま列間の隙間
/// （区切り線1本ぶん）を必ず確保する。
/// <para><b>最後の列は例外で隙間を空けない。</b>その右にあるのは隣の列ではなく縦スクロールバー
/// （テーマで幅7px）と詳細列との GridSplitter で、そこは既に空いている。ここでも隙間を足すと
/// ID の後ろだけ隙間2つぶんに見える。</para></summary>
public sealed class ColumnGapGridViewRowPresenter : GridViewRowPresenter
{
    /// <summary>列と列の間に必ず空ける隙間。列幅を測る側（<see cref="GitSessionView"/>）はこのぶんを
    /// 上乗せした幅を与えるので、文字そのものは省略されない。</summary>
    public const double ColumnGap = 8;

    /// <summary>余白は「最後のセルか」で変わるため、全セルがそろう測定時に入れる
    /// （<see cref="OnVisualChildrenChanged"/> の時点では何個目が最後になるか分からない）。
    /// 同じ値なら代入しないので、レイアウトの無効化が繰り返し走ることはない。</summary>
    protected override Size MeasureOverride(Size constraint)
    {
        var last = VisualChildrenCount - 1;
        for (var i = 0; i <= last; i++)
        {
            if (GetVisualChild(i) is not FrameworkElement cell) continue;
            var margin = new Thickness(0, 0, i == last ? 0 : ColumnGap, 0);
            if (cell.Margin != margin) cell.Margin = margin;
        }

        return base.MeasureOverride(constraint);
    }
}
