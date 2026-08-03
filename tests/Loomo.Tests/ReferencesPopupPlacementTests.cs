using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 使用箇所（Find References）ポップアップの配置（<see cref="ReferencesPopupPlacement"/>）の検証。
/// 回帰の核心は「呼び出したエディタビューに紐づく」「操作していない側の分割を覆わない」
/// 「ウィンドウ外へ出ない」の3点（以前はペイン領域全体の中央固定だった）。
/// 座標系はウィンドウのクライアント領域（左上原点）。
/// </summary>
public class ReferencesPopupPlacementTests
{
    // 1200x800 のウィンドウを左右に分割した想定。
    private static readonly Rect Window = new(0, 0, 1200, 800);
    private static readonly Rect LeftView = new(0, 0, 600, 800);
    private static readonly Rect RightView = new(600, 0, 600, 800);

    private static Rect PlacedRect(Size popup, Rect target, Rect window)
        => new(ReferencesPopupPlacement.Place(popup, target, window), popup);

    [Fact]
    public void 呼び出したビューの中に収まる()
    {
        var popup = new Size(500, 300);

        var placed = PlacedRect(popup, RightView, Window);

        Assert.True(RightView.Contains(placed), $"右分割の外に出た: {placed}");
    }

    [Fact]
    public void 操作していない側の分割を覆わない_回帰()
    {
        var popup = new Size(500, 300);

        var onLeft = PlacedRect(popup, LeftView, Window);
        var onRight = PlacedRect(popup, RightView, Window);

        Assert.False(onLeft.IntersectsWith(RightView), "左のビューから出したのに右へはみ出した");
        Assert.False(onRight.IntersectsWith(LeftView), "右のビューから出したのに左へはみ出した");
        // 出どころが違えば位置も違う（＝どのビューから出たかが位置で判る）。
        Assert.NotEqual(onLeft.Left, onRight.Left);
    }

    // 実測幅は XAML の MaxWidth=760 に張り付くのが常態（省略記号が付いても DesiredSize は縮まない）。
    // 500 幅でしか試していなかったため、「反対側を覆わない」という主張が実寸では崩れていた（レビュー指摘 R1）。
    // 対策は「置き方」ではなく「測る前に幅を基準ビューへ詰める」こと。その両方をここで固定する。
    private const double PreferredMax = 760;   // XAML の MaxWidth
    private const double MinUsable = 460;      // XAML の MinWidth（一覧として読める下限）

    private static Size PopupIn(Rect target, double height = 380)
        => new(ReferencesPopupPlacement.MaxWidthIn(target, PreferredMax, MinUsable), height);

    [Fact]
    public void 上限幅の一覧でも操作していない側の分割を覆わない()
    {
        var onRight = PlacedRect(PopupIn(RightView), RightView, Window);
        var onLeft = PlacedRect(PopupIn(LeftView), LeftView, Window);

        Assert.False(onRight.IntersectsWith(LeftView), $"右から出したのに左を覆った: {onRight}");
        Assert.False(onLeft.IntersectsWith(RightView), $"左から出したのに右を覆った: {onLeft}");
    }

    [Fact]
    public void 幅は基準ビューに収まるまで詰める()
    {
        // 分割（600幅）では 760 のままだと必ずはみ出す → ビュー幅 − 余白 に詰める。
        Assert.Equal(RightView.Width - ReferencesPopupPlacement.Margin * 2,
            ReferencesPopupPlacement.MaxWidthIn(RightView, PreferredMax, MinUsable));

        // 広いビューでは上限 760 のまま（無闇に狭めない）。
        Assert.Equal(PreferredMax,
            ReferencesPopupPlacement.MaxWidthIn(new Rect(0, 0, 1600, 800), PreferredMax, MinUsable));
    }

    [Fact]
    public void 読めない幅までは詰めない()
    {
        // ビューが下限より狭い場合だけは、はみ出しを許してでも読める幅を優先する。
        var sliver = new Rect(0, 0, 200, 800);

        Assert.Equal(MinUsable, ReferencesPopupPlacement.MaxWidthIn(sliver, PreferredMax, MinUsable));
    }

    [Fact]
    public void ビューの下端へ寄せる()
    {
        var popup = new Size(500, 300);

        var placed = PlacedRect(popup, LeftView, Window);

        Assert.Equal(LeftView.Bottom - ReferencesPopupPlacement.Margin, placed.Bottom);
        Assert.Equal(LeftView.Left + ReferencesPopupPlacement.Margin, placed.Left);
    }

    [Fact]
    public void ビューより背が高ければ上端から出す()
    {
        var view = new Rect(0, 300, 600, 200);   // 縦に狭いビュー
        var popup = new Size(500, 300);

        var placed = PlacedRect(popup, view, Window);

        Assert.Equal(view.Top + ReferencesPopupPlacement.Margin, placed.Top);
    }

    [Fact]
    public void ウィンドウ外にはみ出さない()
    {
        // ウィンドウ右下の端に張り付いたビュー（ステージの袖など）から、幅の広い一覧を出す。
        var view = new Rect(1000, 700, 200, 100);
        var popup = new Size(760, 380);

        var placed = PlacedRect(popup, view, Window);

        Assert.True(placed.Right <= Window.Right, $"右へはみ出した: {placed}");
        Assert.True(placed.Bottom <= Window.Bottom, $"下へはみ出した: {placed}");
        Assert.True(placed.Left >= Window.Left && placed.Top >= Window.Top, $"左上へはみ出した: {placed}");
    }

    [Fact]
    public void ウィンドウより大きいポップアップは左上に寄せる()
    {
        var small = new Rect(0, 0, 400, 300);
        var popup = new Size(2000, 2000);

        var placed = ReferencesPopupPlacement.Place(popup, small, small);

        Assert.Equal(ReferencesPopupPlacement.Margin, placed.X);
        Assert.Equal(ReferencesPopupPlacement.Margin, placed.Y);
    }

    [Fact]
    public void OffsetFromはビュー左上からの相対値を返す()
    {
        var popup = new Size(500, 300);

        var placed = ReferencesPopupPlacement.Place(popup, RightView, Window);
        var offset = ReferencesPopupPlacement.OffsetFrom(popup, RightView, Window);

        Assert.Equal(placed.X - RightView.Left, offset.X);
        Assert.Equal(placed.Y - RightView.Top, offset.Y);
    }
}
