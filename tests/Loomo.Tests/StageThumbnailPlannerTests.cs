using System.Windows;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class StageThumbnailPlannerTests
{
    private const double CardAspect = 3.0 / 2.0;

    [Theory]
    [InlineData(PaneKind.Editor)]
    [InlineData(PaneKind.Terminal)]
    [InlineData(PaneKind.Browser)]
    [InlineData(PaneKind.EditorSupport)]
    [InlineData(PaneKind.Git)]
    [InlineData(PaneKind.Diff)]
    public void Native_panes_keep_live_visual_thumbnails(PaneKind kind)
        => Assert.False(StageThumbnailPlanner.UsesSnapshotThumbnail(kind));

    /// <summary>これが本体の回帰テスト。描画元を Main 実寸で組むと、ペイン 1 枚ごとの
    /// 実寸レイアウトでペイン切替が 100ms を超える（かつミニチュアが点に潰れる）。
    /// 広さが違っても描画元サイズは変わらないこと。</summary>
    [Fact]
    public void Source_size_does_not_scale_with_the_main_area()
    {
        var narrow = StageThumbnailPlanner.SourceSize(780, CardAspect);
        var wide = StageThumbnailPlanner.SourceSize(1433, CardAspect);
        var huge = StageThumbnailPlanner.SourceSize(3840, CardAspect);

        Assert.Equal(narrow, wide);
        Assert.Equal(narrow, huge);
        Assert.Equal(StageThumbnailPlanner.VirtualWidth, wide.Width);
    }

    [Fact]
    public void Source_size_keeps_the_card_aspect()
    {
        var size = StageThumbnailPlanner.SourceSize(1433, CardAspect);

        Assert.Equal(800, size.Width);
        Assert.Equal(800 / CardAspect, size.Height);
    }

    [Fact]
    public void Available_width_never_makes_the_source_smaller_than_the_widest_card()
    {
        var size = StageThumbnailPlanner.SourceSize(300, CardAspect);

        Assert.Equal(StageThumbnailPlanner.VirtualWidth, size.Width);
        Assert.True(size.Width > 790);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Unmeasured_available_width_falls_back_to_the_virtual_width(double availableWidth)
    {
        var size = StageThumbnailPlanner.SourceSize(availableWidth, CardAspect);

        Assert.Equal(StageThumbnailPlanner.VirtualWidth, size.Width);
        Assert.True(size.Height > 0);
    }

    [Fact]
    public void Degenerate_aspect_still_produces_a_usable_size()
    {
        var size = StageThumbnailPlanner.SourceSize(420, 0);

        Assert.Equal(StageThumbnailPlanner.VirtualWidth, size.Width);
        Assert.Equal(StageThumbnailPlanner.VirtualWidth, size.Height);
    }

    /// <summary>仮想幅で頭打ちになる範囲のリサイズでは、袖を作り直さない
    /// （リサイズのたびに全ペインを実寸レイアウトし直していたのが重さの一因）。</summary>
    [Fact]
    public void Resizing_above_the_virtual_width_does_not_change_the_source_size()
    {
        Assert.False(StageThumbnailPlanner.SourceSizeChanged(1433, 1420));
        Assert.False(StageThumbnailPlanner.SourceSizeChanged(1420, 3840));
    }

    [Fact]
    public void Resizing_does_not_change_the_fixed_source_size()
    {
        Assert.False(StageThumbnailPlanner.SourceSizeChanged(300, 1400));
        Assert.False(StageThumbnailPlanner.SourceSizeChanged(1400, 300));
        Assert.False(StageThumbnailPlanner.SourceSizeChanged(300, 360));
    }

    [Fact]
    public void Never_built_always_reports_changed()
    {
        Assert.True(StageThumbnailPlanner.SourceSizeChanged(0, 1433));
    }

    private static readonly PaneKind[] OffStage =
    [
        PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport,
        PaneKind.Git, PaneKind.Diff, PaneKind.Ai,
    ];

    /// <summary>本体の回帰テスト。舞台を Editor→Terminal に切り替えても、袖の出入りは 2 枚だけ。
    /// 以前は毎回 8 枚すべてを外して繋ぎ直しており、その付け替え自体が切替の主因だった。</summary>
    [Fact]
    public void Switching_the_stage_pane_only_moves_the_two_panes_that_changed()
    {
        var required = new[]
        {
            PaneKind.Editor, PaneKind.Browser, PaneKind.EditorSupport,
            PaneKind.Git, PaneKind.Diff, PaneKind.Ai,
        };

        var plan = StageThumbnailPlanner.PlanSources(OffStage, OffStage, required);

        Assert.Equal(new[] { PaneKind.Editor }, plan.Add);
        Assert.Equal(new[] { PaneKind.Terminal }, plan.Remove);
        Assert.Equal(5, plan.Keep.Count);
        Assert.DoesNotContain(PaneKind.Terminal, plan.Keep);
    }

    [Fact]
    public void Nothing_moves_when_the_required_set_is_unchanged()
    {
        var plan = StageThumbnailPlanner.PlanSources(OffStage, OffStage, OffStage);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Remove);
        Assert.Equal(OffStage, plan.Keep);
    }

    /// <summary>仮想サイズが変わったときは再利用できない（呼び出し側が reusable を空で渡す）
    /// ＝全部組み直す。</summary>
    [Fact]
    public void Nothing_is_reusable_rebuilds_every_source()
    {
        var plan = StageThumbnailPlanner.PlanSources(OffStage, Array.Empty<PaneKind>(), OffStage);

        Assert.Equal(OffStage, plan.Add);
        Assert.Equal(OffStage, plan.Remove);
        Assert.Empty(plan.Keep);
    }

    /// <summary>他の経路（タイル再構築・ワークスペース切替など）でホストからペインが外されていたら、
    /// required に居ても作り直す。差分が壊れて空のミニチュアが残らないための保険。</summary>
    [Fact]
    public void A_source_that_lost_its_pane_is_rebuilt_even_when_still_required()
    {
        var reusable = OffStage.Where(kind => kind != PaneKind.Git).ToArray();

        var plan = StageThumbnailPlanner.PlanSources(OffStage, reusable, OffStage);

        Assert.Equal(new[] { PaneKind.Git }, plan.Add);
        Assert.Equal(new[] { PaneKind.Git }, plan.Remove);
        Assert.DoesNotContain(PaneKind.Git, plan.Keep);
    }

    [Fact]
    public void Sources_that_are_no_longer_required_are_removed_without_being_re_added()
    {
        var required = new[] { PaneKind.Terminal, PaneKind.Browser };

        var plan = StageThumbnailPlanner.PlanSources(OffStage, OffStage, required);

        Assert.Empty(plan.Add);
        Assert.Equal(
            new[] { PaneKind.EditorSupport, PaneKind.Git, PaneKind.Diff, PaneKind.Ai },
            plan.Remove);
        Assert.Equal(required, plan.Keep);
    }

    [Fact]
    public void First_build_adds_everything_and_removes_nothing()
    {
        var plan = StageThumbnailPlanner.PlanSources(
            Array.Empty<PaneKind>(), Array.Empty<PaneKind>(), OffStage);

        Assert.Equal(OffStage, plan.Add);
        Assert.Empty(plan.Remove);
        Assert.Empty(plan.Keep);
    }
}
