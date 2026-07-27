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
/// <para>どちらも固定仮想幅で解ける。<see cref="VirtualWidth"/>=420 は袖 180px で 0.43 倍＝
/// 内容が判別できる縮尺として実機検証済みの値。</para>
/// </summary>
public static class StageThumbnailPlanner
{
    /// <summary>描画元をレイアウトする固定仮想幅。Main がこれより広くても追従しない。</summary>
    public const double VirtualWidth = 420;

    /// <summary>描画元のサイズを決める。<paramref name="availableWidth"/> が仮想幅より狭いときだけ
    /// そちらに合わせる（実際の表示より広く組んでも縮小率が上がるだけで得がない）。</summary>
    public static Size SourceSize(double availableWidth, double cardAspect)
    {
        var width = ResolveWidth(availableWidth);
        var aspect = cardAspect > 0 ? cardAspect : 1;
        return new Size(Math.Max(width, 1), Math.Max(width / aspect, 1));
    }

    /// <summary>幅の変化が描画元サイズを変えるかどうか。仮想幅で頭打ちになるので、
    /// 420 を超える範囲でのウィンドウ／スプリッタのリサイズでは作り直さなくてよい。</summary>
    public static bool SourceSizeChanged(double previousWidth, double newWidth)
        => previousWidth <= 0                                   // まだ一度も組んでいない
        || Math.Abs(ResolveWidth(previousWidth) - ResolveWidth(newWidth)) > 1;

    private static double ResolveWidth(double availableWidth)
        => availableWidth > 0 ? Math.Min(VirtualWidth, availableWidth) : VirtualWidth;

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
