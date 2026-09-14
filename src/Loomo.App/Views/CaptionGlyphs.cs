using System.Windows.Media;

namespace sk0ya.Loomo.App.Views;

/// <summary>自前キャプションの最大化／元に戻すアイコン。本体・切り離し・設定の各ウィンドウで共有し、見た目を揃える。</summary>
internal static class CaptionGlyphs
{
    public static readonly Geometry Maximize = Freeze(Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z"));
    public static readonly Geometry Restore = Freeze(Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"));

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
