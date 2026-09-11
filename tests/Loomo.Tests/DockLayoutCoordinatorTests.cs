using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ドックモード（袖なし＝IDE 風ツールウィンドウ）の状態機械。</summary>
public class DockLayoutCoordinatorTests
{
    private static DockLayoutCoordinator Active()
    {
        var dock = new DockLayoutCoordinator();
        dock.Enter();
        dock.EnsureCenterPane(_ => true);
        return dock;
    }

    [Theory]
    [InlineData(PaneKind.Editor, DockRegion.Center)]
    [InlineData(PaneKind.Browser, DockRegion.Center)]
    [InlineData(PaneKind.Diff, DockRegion.Center)]
    [InlineData(PaneKind.Ai, DockRegion.Center)]
    [InlineData(PaneKind.EditorSupport, DockRegion.Right)]
    [InlineData(PaneKind.Terminal, DockRegion.Bottom)]
    [InlineData(PaneKind.Git, DockRegion.Bottom)]
    [InlineData(PaneKind.Debug, DockRegion.Bottom)]
    [InlineData(PaneKind.TsIde, DockRegion.Bottom)]
    [InlineData(PaneKind.Search, DockRegion.Bottom)]
    [InlineData(PaneKind.Files, DockRegion.Bottom)]
    public void Default_regions(PaneKind kind, DockRegion expected)
        => Assert.Equal(expected, Active().RegionOf(kind));

    /// <summary>帯に並ぶ面は全部どこかの領域に居て、既定の割り当ても漏れが無い。</summary>
    [Fact]
    public void Every_docked_pane_kind_has_a_place()
    {
        var dock = Active();
        foreach (var kind in DockLayoutCoordinator.DockOrder)
        {
            Assert.Contains(kind, DockLayoutCoordinator.DefaultRegions.Keys);
            Assert.True(dock.IsInTile(kind) || dock.IsDocked(kind));
        }
    }

    /// <summary>トレースはドックに出さない。
    /// <para>部屋の面としては出しておらず（集中モードの並びにもビュー・スイッチャーにも居ない）、
    /// ドックの帯だけがその面を出す唯一の入口になっていた——ドック状態にしただけで TRACE が
    /// 現れるのはそれが理由。帯にも既定の割り当てにも置かない。</para></summary>
    [Fact]
    public void Trace_never_appears_in_the_dock()
    {
        Assert.DoesNotContain(PaneKind.Trace, DockLayoutCoordinator.DockOrder);
        Assert.DoesNotContain(PaneKind.Trace, DockLayoutCoordinator.DefaultRegions.Keys);
        Assert.False(DockLayoutCoordinator.IsDockable(PaneKind.Trace));

        var dock = Active();
        Assert.DoesNotContain(PaneKind.Trace, dock.PanesIn(DockRegion.Center));
        Assert.DoesNotContain(PaneKind.Trace, dock.PanesIn(DockRegion.Bottom));
        Assert.DoesNotContain(PaneKind.Trace, dock.PanesIn(DockRegion.Right));
        Assert.DoesNotContain(PaneKind.Trace, dock.OpenPanes());
    }

    /// <summary>トレースを抱えた古い保存（既定に居た頃の部屋）を復元しても出さない。
    /// <para>帯に出ない面の <c>RegionOf</c> は既定の中央を返すので、保存された割り当てを
    /// そのまま信じると取っ手の無い面が中央に立ってしまう。保存へも書き戻さない。</para></summary>
    [Fact]
    public void Restore_drops_a_saved_trace_placement()
    {
        var dock = new DockLayoutCoordinator();
        dock.Restore(
            active: true,
            regions: [new(PaneKind.Trace, DockRegion.Bottom)],
            centerPane: PaneKind.Trace,
            centerClosed: false,
            bottomPane: PaneKind.Trace,
            rightPane: null,
            bottomHeight: null,
            rightWidth: null);
        dock.DropInapplicable(_ => true);

        Assert.Equal(PaneKind.Editor, dock.CenterPane);   // 中央は並び順の先頭が立つ
        Assert.Null(dock.BottomPane);
        Assert.DoesNotContain(PaneKind.Trace, dock.OpenPanes());
        Assert.DoesNotContain(PaneKind.Trace, dock.ChangedRegions().Select(pair => pair.Key));
    }

    /// <summary>中央も「同時に1枚」。立て替えても居るのは1枚だけ。</summary>
    [Fact]
    public void Center_holds_one_pane_at_a_time()
    {
        var dock = Active();
        Assert.Equal(PaneKind.Editor, dock.CenterPane);   // 並び順の先頭＝エディタ

        Assert.True(dock.Toggle(PaneKind.Browser));
        Assert.Equal(PaneKind.Browser, dock.CenterPane);
        Assert.True(dock.IsOpen(PaneKind.Browser));
        Assert.False(dock.IsOpen(PaneKind.Editor));      // 中央も1枚だけ
    }

    /// <summary>中央も畳める。
    /// <para>下や右に面が出ていようと出ていまいと、閉じると言われたら閉じる。ここで次の面を
    /// 繰り上げていたせいで、閉じたはずの中央が別の面に切り替わるだけになっていた。</para></summary>
    [Fact]
    public void Closing_the_center_leaves_it_empty()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Terminal);                  // 下に道具を1つ出しておく
        Assert.Equal(PaneKind.Editor, dock.CenterPane);

        Assert.True(dock.Close(DockRegion.Center));
        Assert.Null(dock.CenterPane);
        Assert.True(dock.CenterClosed);
        Assert.Equal(PaneKind.Terminal, dock.BottomPane);   // 下はそのまま

        // 組み立てのたびに呼ばれる場所。閉じた中央を埋め直さない。
        dock.EnsureCenterPane(_ => true);
        Assert.Null(dock.CenterPane);
    }

    /// <summary>出ている面は<b>中央→下→右</b>の順で数える。畳んだ面から現在地（フォーカス）を
    /// 渡す先も、親から外してはいけない面も、この並びで決まる。</summary>
    [Fact]
    public void Open_panes_are_listed_center_first()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Terminal);
        dock.Toggle(PaneKind.EditorSupport);

        Assert.Equal(
            new[] { PaneKind.Editor, PaneKind.Terminal, PaneKind.EditorSupport },
            dock.OpenPanes());

        // 中央を畳んでも、残って見えている面は順に並ぶ（＝渡す先がある）。
        dock.Close(DockRegion.Center);
        Assert.Equal(new[] { PaneKind.Terminal, PaneKind.EditorSupport }, dock.OpenPanes());

        dock.Close(DockRegion.Bottom);
        dock.Close(DockRegion.Right);
        Assert.Empty(dock.OpenPanes());   // 1枚も出ていない＝現在地ごと捨てる場面
    }

    /// <summary>モードの外では何も出ていない（分割・集中の見え方をドックの状態で語らない）。</summary>
    [Fact]
    public void Inactive_reports_no_open_panes()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Terminal);
        dock.Exit();

        Assert.Empty(dock.OpenPanes());
    }

    /// <summary>帯のアイコンからも同じ（出ている面をもう一度押す＝畳む）。戻せば印も下りる。</summary>
    [Fact]
    public void The_center_icon_closes_and_reopens()
    {
        var dock = Active();

        Assert.True(dock.Toggle(PaneKind.Editor));
        Assert.Null(dock.CenterPane);
        Assert.True(dock.CenterClosed);

        Assert.True(dock.Toggle(PaneKind.Browser));
        Assert.Equal(PaneKind.Browser, dock.CenterPane);
        Assert.False(dock.CenterClosed);
    }

    /// <summary>閉じてあったことは次の起動へ持ち越す（開き直すのは押した人の仕事）。</summary>
    [Fact]
    public void A_closed_center_stays_closed_across_restore()
    {
        var dock = new DockLayoutCoordinator();
        dock.Restore(
            active: true,
            regions: null,
            centerPane: null,
            centerClosed: true,
            bottomPane: PaneKind.Terminal,
            rightPane: null,
            bottomHeight: null,
            rightWidth: null);

        dock.DropInapplicable(_ => true);

        Assert.Null(dock.CenterPane);
        Assert.True(dock.CenterClosed);
        Assert.Equal(PaneKind.Terminal, dock.BottomPane);
    }

    /// <summary>中央の面を下／右へ移したら、中央には次の面が立つ（空にはしない）。</summary>
    [Fact]
    public void Moving_the_center_pane_away_promotes_another()
    {
        var dock = Active();
        Assert.Equal(PaneKind.Editor, dock.CenterPane);

        dock.Place(PaneKind.Editor, DockRegion.Bottom);
        Assert.Null(dock.CenterPane);            // 移した直後は空
        dock.EnsureCenterPane(_ => true);
        Assert.Equal(PaneKind.Browser, dock.CenterPane);
        Assert.Equal(PaneKind.Editor, dock.BottomPane);
    }

    /// <summary>中央に置ける面が1つも無ければ空のまま（案内を出す側の判断に委ねる）。</summary>
    [Fact]
    public void Center_stays_empty_when_nothing_can_live_there()
    {
        var dock = Active();
        foreach (var kind in dock.PanesIn(DockRegion.Center).ToList())
            dock.Place(kind, DockRegion.Bottom);
        dock.EnsureCenterPane(_ => true);

        Assert.Null(dock.CenterPane);
    }

    /// <summary>ドックモードでないうちは、どのペインもタイルから抜けない
    /// （割り当ては持っていても、分割・集中モードの見え方を変えてはいけない）。</summary>
    [Fact]
    public void Inactive_keeps_every_pane_in_the_tile()
    {
        var dock = new DockLayoutCoordinator();
        Assert.True(dock.IsInTile(PaneKind.Git));
        Assert.False(dock.IsOpen(PaneKind.Git));
        Assert.False(dock.Toggle(PaneKind.Git) && dock.IsOpen(PaneKind.Git));
    }

    [Fact]
    public void Toggle_opens_then_collapses()
    {
        var dock = Active();
        Assert.True(dock.Toggle(PaneKind.Git));
        Assert.Equal(PaneKind.Git, dock.BottomPane);
        Assert.True(dock.IsOpen(PaneKind.Git));

        Assert.True(dock.Toggle(PaneKind.Git));
        Assert.Null(dock.BottomPane);
        Assert.False(dock.IsOpen(PaneKind.Git));
    }

    /// <summary>同じ領域は1枚だけ。別の面を押したら差し替わる（アイコンがタブを兼ねる）。</summary>
    [Fact]
    public void One_pane_per_region()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Git);
        dock.Toggle(PaneKind.Terminal);

        Assert.Equal(PaneKind.Terminal, dock.BottomPane);
        Assert.False(dock.IsOpen(PaneKind.Git));
    }

    /// <summary>下と右は独立して開ける。</summary>
    [Fact]
    public void Regions_are_independent()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Git);
        dock.Toggle(PaneKind.EditorSupport);

        Assert.Equal(PaneKind.Git, dock.BottomPane);
        Assert.Equal(PaneKind.EditorSupport, dock.RightPane);
    }

    /// <summary>出ていた面を移したら、行き先で開き直す（畳んだままだとどこへ消えたのか判らない）。</summary>
    [Fact]
    public void Moving_an_open_pane_reopens_it_in_the_new_region()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Git);

        Assert.True(dock.Place(PaneKind.Git, DockRegion.Right));
        Assert.Null(dock.BottomPane);
        Assert.Equal(PaneKind.Git, dock.RightPane);
        Assert.True(dock.IsOpen(PaneKind.Git));
    }

    /// <summary>中央へ移したら領域からは消え、そのまま中央に立つ。</summary>
    [Fact]
    public void Moving_to_center_puts_the_pane_on_the_center()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Git);

        Assert.True(dock.Place(PaneKind.Git, DockRegion.Center));
        Assert.Null(dock.BottomPane);
        Assert.True(dock.IsInTile(PaneKind.Git));
        Assert.False(dock.IsDocked(PaneKind.Git));
        Assert.Equal(PaneKind.Git, dock.CenterPane);
    }

    [Fact]
    public void Panes_in_a_region_follow_the_bar_order()
    {
        var bottom = Active().PanesIn(DockRegion.Bottom).ToList();
        Assert.Equal(
            DockLayoutCoordinator.DockOrder.Where(bottom.Contains).ToList(),
            bottom);
    }

    /// <summary>保存は既定との差分だけ（既定を変えたときに保存済みの部屋が置いていかれないように）。</summary>
    [Fact]
    public void Only_changed_regions_are_persisted()
    {
        var dock = Active();
        Assert.Empty(dock.ChangedRegions());

        dock.Place(PaneKind.Git, DockRegion.Right);
        var changed = dock.ChangedRegions().ToList();
        Assert.Single(changed);
        Assert.Equal(PaneKind.Git, changed[0].Key);
        Assert.Equal(DockRegion.Right, changed[0].Value);
    }

    [Fact]
    public void Restore_applies_saved_regions_over_the_defaults()
    {
        var dock = new DockLayoutCoordinator();
        dock.Restore(
            active: true,
            regions: new[] { new KeyValuePair<PaneKind, DockRegion>(PaneKind.Ai, DockRegion.Right) },
            centerPane: PaneKind.Browser,
            centerClosed: false,
            bottomPane: PaneKind.Terminal,
            rightPane: PaneKind.Ai,
            bottomHeight: 300,
            rightWidth: 400);

        Assert.True(dock.Active);
        Assert.Equal(DockRegion.Right, dock.RegionOf(PaneKind.Ai));
        Assert.Equal(DockRegion.Bottom, dock.RegionOf(PaneKind.Git));   // 保存に無い面は既定のまま
        Assert.Equal(PaneKind.Browser, dock.CenterPane);
        Assert.Equal(PaneKind.Terminal, dock.BottomPane);
        Assert.Equal(PaneKind.Ai, dock.RightPane);
        Assert.Equal(300, dock.BottomHeight);
        Assert.Equal(400, dock.RightWidth);
    }

    /// <summary>領域と食い違う「開いていた面」は捨てる（割り当てを変えた古い保存を復元したとき）。</summary>
    [Fact]
    public void Restore_drops_open_panes_that_no_longer_live_there()
    {
        var dock = new DockLayoutCoordinator();
        dock.Restore(
            active: true,
            regions: new[] { new KeyValuePair<PaneKind, DockRegion>(PaneKind.Git, DockRegion.Right) },
            centerPane: PaneKind.Terminal,   // 中央の面ではない
            centerClosed: false,
            bottomPane: PaneKind.Git,     // もう下には居ない
            rightPane: PaneKind.Terminal, // もともと右ではない
            bottomHeight: null,
            rightWidth: null);

        Assert.Null(dock.CenterPane);
        Assert.Null(dock.BottomPane);
        Assert.Null(dock.RightPane);
        Assert.Equal(DockLayoutCoordinator.DefaultBottomHeight, dock.BottomHeight);
        Assert.Equal(DockLayoutCoordinator.DefaultRightWidth, dock.RightWidth);
    }

    /// <summary>その部屋に無いペイン（C# の無い部屋の IDE 等）を開いたままにしない。
    /// 中央がそれだった場合は、代わりに立てられる面へ差し替える。</summary>
    [Fact]
    public void Inapplicable_open_panes_are_dropped()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Debug);
        Assert.Equal(PaneKind.Debug, dock.BottomPane);

        dock.DropInapplicable(kind => kind != PaneKind.Debug);
        Assert.Null(dock.BottomPane);

        dock.Place(PaneKind.Debug, DockRegion.Center);
        Assert.Equal(PaneKind.Debug, dock.CenterPane);
        dock.DropInapplicable(kind => kind != PaneKind.Debug);
        Assert.NotNull(dock.CenterPane);
        Assert.NotEqual(PaneKind.Debug, dock.CenterPane);
    }

    [Fact]
    public void Sizes_are_clamped()
    {
        var dock = Active();
        dock.SetBottomHeight(5);
        dock.SetRightWidth(10_000);

        Assert.Equal(DockLayoutCoordinator.MinBottomHeight, dock.BottomHeight);
        Assert.Equal(DockLayoutCoordinator.MaxRightWidth, dock.RightWidth);
    }

    /// <summary>モードを出ても割り当てと開いていた面は保つ（戻ったら同じ部屋が戻る）。</summary>
    [Fact]
    public void Exiting_keeps_the_arrangement()
    {
        var dock = Active();
        dock.Toggle(PaneKind.Git);
        dock.Place(PaneKind.Ai, DockRegion.Right);

        Assert.True(dock.Exit());
        Assert.False(dock.Active);
        Assert.Equal(PaneKind.Git, dock.BottomPane);
        Assert.Equal(DockRegion.Right, dock.RegionOf(PaneKind.Ai));

        Assert.True(dock.Enter());
        Assert.True(dock.IsOpen(PaneKind.Git));
    }
}
