namespace sk0ya.Loomo.App.Layout;

/// <summary>
/// 袖（ミニチュア）の描画元を「どのサイズで実寸レイアウトするか」の決定（UI 非依存）。
///
/// <para>ミニチュアは実コントロールを Grid へ実寸レイアウトし、VisualBrush で縮小して描く。
/// この描画元を Main 領域の実寸（例 1433x951）で組むと 2 つ同時に壊れる：</para>
/// <list type="number">
/// <item>内容が左上の点へ潰れて何のペインか判別できない（1366→760 でも足りない）。</item>
/// <item>ペイン 1 枚ごとに実寸の Measure/Arrange/UpdateLayout が走り、ペイン切替 1 回が 100ms を超える。
/// コストはほぼ面積比で効く（実測：Git ペイン単体で 780 幅 25ms → 1433 幅 54ms）。</item>
/// </list>
/// <para>どちらも固定仮想幅で解ける。<see cref="VirtualWidth"/>=560 は最大袖カード 550px より
/// 常に大きいため、袖表示が元の描画より拡大されることはない。</para>
/// </summary>
public static class StageThumbnailPlanner
{
    /// <summary>描画元をレイアウトする固定仮想幅。Main がこれより広くても追従しない。</summary>
    public const double VirtualWidth = 560;

    /// <summary>
    /// ライブ VisualBrush ではなくスナップショットを使うペイン。
    /// Browser は同じ URL を表示する袖専用 WebView2、EditorSupport は他ペインと同じ
    /// VisualBrush を使うため、現在は該当なし。
    /// </summary>
    public static bool UsesSnapshotThumbnail(PaneKind kind)
        => false;

    /// <summary>描画元のサイズを決める。袖の最大幅より常に大きい固定サイズを使い、
    /// ウィンドウ幅にかかわらずサムネイルが拡大表示されないようにする。</summary>
    public static Size SourceSize(double availableWidth, double cardAspect)
    {
        var width = ResolveWidth(availableWidth);
        var aspect = cardAspect > 0 ? cardAspect : 1;
        return new Size(Math.Max(width, 1), Math.Max(width / aspect, 1));
    }

    /// <summary>描画元は固定サイズなので、未構築のときだけ構築が必要。</summary>
    public static bool SourceSizeChanged(double previousWidth, double newWidth)
        => previousWidth <= 0;

    private static double ResolveWidth(double availableWidth) => VirtualWidth;

    /// <summary>描画元ホストの作り直しを最小化する差分。<see cref="Keep"/> は一切触らない
    /// （＝親の付け替えもレイアウトも走らない）。</summary>
    public readonly record struct ThumbnailSourcePlan(
        IReadOnlyList<PaneKind> Remove,
        IReadOnlyList<PaneKind> Add,
        IReadOnlyList<PaneKind> Keep);

    /// <summary>今ある描画元ホストを、必要な集合へ最小手数で寄せる計画を立てる。
    ///
    /// <para>ペインの親を付け替える行為そのものが高い（実測：Git 15ms / Diff 20ms / TsIde 40ms。
    /// Measure/Arrange と同等かそれ以上）。舞台を Editor→Terminal に切り替えても袖の出入りは 2 枚だけなのに、
    /// 以前は毎回 8 枚すべてを外して繋ぎ直していた。</para></summary>
    /// <param name="tracked">今ホストを持っているペイン。</param>
    /// <param name="reusable">そのうち「そのまま使える」もの
    /// （ホストが健在で、仮想サイズも変わっていないもの）。</param>
    /// <param name="required">今回ミニチュアが必要なペイン。</param>
    public static ThumbnailSourcePlan PlanSources(
        IReadOnlyCollection<PaneKind> tracked,
        IReadOnlyCollection<PaneKind> reusable,
        IReadOnlyCollection<PaneKind> required)
    {
        var keep = required.Where(reusable.Contains).ToList();
        return new ThumbnailSourcePlan(
            Remove: tracked.Where(kind => !keep.Contains(kind)).ToList(),
            Add: required.Where(kind => !keep.Contains(kind)).ToList(),
            Keep: keep);
    }
}
