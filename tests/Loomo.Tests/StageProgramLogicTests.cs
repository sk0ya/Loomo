using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 配役モードの純ロジック（Ctrl+T 巡回・袖/主役/サブの入れ替え・終了条件）の検証。
/// UI（舞台の見た目）は並列 STA で BAML 競合しフレーキーなので、状態遷移だけ純関数で押さえる。
/// </summary>
public class StageProgramLogicTests
{
    // 袖・俯瞰の並び順（ShellWindow.Stage.cs の StageOrder と同等）。
    private static readonly PaneKind[] Order =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport,
        PaneKind.Git, PaneKind.Diff, PaneKind.Ai,
    ];

    private static StageState State(PaneKind main, params StageSub[] subs)
        => new(main, subs.ToList());

    private static StageSub Sub(PaneKind kind, StageDock dock = StageDock.Right) => new(kind, dock);

    // ===== 派生状態 =====

    [Fact]
    public void No_subs_means_not_program_active()
    {
        var s = State(PaneKind.Editor);
        Assert.False(s.ProgramActive);
        Assert.Single(s.OnStage);
    }

    [Fact]
    public void One_sub_means_program_active()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal));
        Assert.True(s.ProgramActive);
        Assert.True(s.IsOnStage(PaneKind.Terminal));
        Assert.True(s.IsOnStage(PaneKind.Editor));
        Assert.False(s.IsOnStage(PaneKind.Git));
    }

    // ===== Ctrl+T 巡回（主役固定・末尾サブを回す） =====

    [Fact]
    public void Cycle_advances_last_sub_keeping_main()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal));
        var next = StageProgramLogic.NextSubCycle(s, Order, +1);

        Assert.Equal(PaneKind.Editor, next.Main);          // 主役は不変
        Assert.Equal(PaneKind.Browser, next.Subs[^1].Kind); // Terminal の次（Editor は主役で飛ばす）
    }

    [Fact]
    public void Cycle_skips_panes_already_on_stage()
    {
        // 主役 Editor、サブに Terminal と Browser。末尾 Browser を進めると次は EditorSupport
        // （Editor は主役、Terminal は先行サブなので飛ばす）。
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal), Sub(PaneKind.Browser));
        var next = StageProgramLogic.NextSubCycle(s, Order, +1);

        Assert.Equal(PaneKind.Terminal, next.Subs[0].Kind);     // 先行サブは不変
        Assert.Equal(PaneKind.EditorSupport, next.Subs[1].Kind);
    }

    [Fact]
    public void Cycle_keeps_sub_dock()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal, StageDock.Bottom));
        var next = StageProgramLogic.NextSubCycle(s, Order, +1);
        Assert.Equal(StageDock.Bottom, next.Subs[^1].Dock);
    }

    [Fact]
    public void Cycle_backwards_wraps()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal));
        var next = StageProgramLogic.NextSubCycle(s, Order, -1);
        // Terminal の前は Editor（主役で飛ばす）→ その前の Ai へ巻き戻る。
        Assert.Equal(PaneKind.Ai, next.Subs[^1].Kind);
    }

    // ===== 入れ替え（袖 → スロット） =====

    [Fact]
    public void Wing_onto_main_promotes_and_drops_old_main()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal));
        var r = StageProgramLogic.ApplySwap(s, new WingSlot(PaneKind.Git), new MainSlot());

        Assert.Equal(PaneKind.Git, r.Main);
        Assert.False(r.IsOnStage(PaneKind.Editor));        // 旧主役は袖へ降りる
        Assert.Equal(PaneKind.Terminal, r.Subs.Single().Kind);
    }

    [Fact]
    public void Wing_onto_sub_replaces_that_sub()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal, StageDock.Bottom));
        var r = StageProgramLogic.ApplySwap(s, new WingSlot(PaneKind.Git), new SubSlot(0));

        Assert.Equal(PaneKind.Git, r.Subs[0].Kind);
        Assert.Equal(StageDock.Bottom, r.Subs[0].Dock);     // ドックは保持
        Assert.False(r.IsOnStage(PaneKind.Terminal));       // 旧サブは降りる
    }

    // ===== 入れ替え（スロット ↔ スロット） =====

    [Fact]
    public void Sub_onto_main_swaps()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal));
        var r = StageProgramLogic.ApplySwap(s, new SubSlot(0), new MainSlot());

        Assert.Equal(PaneKind.Terminal, r.Main);
        Assert.Equal(PaneKind.Editor, r.Subs[0].Kind);      // 旧主役がサブへ
    }

    [Fact]
    public void Sub_onto_sub_swaps_kinds_keeping_docks()
    {
        var s = State(PaneKind.Editor,
            Sub(PaneKind.Terminal, StageDock.Right), Sub(PaneKind.Browser, StageDock.Bottom));
        var r = StageProgramLogic.ApplySwap(s, new SubSlot(0), new SubSlot(1));

        Assert.Equal(PaneKind.Browser, r.Subs[0].Kind);
        Assert.Equal(StageDock.Right, r.Subs[0].Dock);
        Assert.Equal(PaneKind.Terminal, r.Subs[1].Kind);
        Assert.Equal(StageDock.Bottom, r.Subs[1].Dock);
    }

    // ===== 迎え入れ・降ろし・終了条件 =====

    [Fact]
    public void AddSub_enters_program_mode()
    {
        var s = State(PaneKind.Editor);
        var r = StageProgramLogic.AddSub(s, PaneKind.Terminal, StageDock.Right);

        Assert.True(r.ProgramActive);
        Assert.Equal(PaneKind.Terminal, r.Subs.Single().Kind);
    }

    [Fact]
    public void AddSub_ignores_main_and_caps_at_two()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal), Sub(PaneKind.Browser));
        Assert.Equal(2, StageProgramLogic.AddSub(s, PaneKind.Editor, StageDock.Right).Subs.Count); // 主役は無視
        var capped = StageProgramLogic.AddSub(s, PaneKind.Git, StageDock.Right);
        Assert.Equal(2, capped.Subs.Count);                 // 上限2、最古を押し出す
        Assert.Equal(PaneKind.Browser, capped.Subs[0].Kind);
        Assert.Equal(PaneKind.Git, capped.Subs[1].Kind);
    }

    [Fact]
    public void Removing_last_sub_ends_program_mode()
    {
        var s = State(PaneKind.Editor, Sub(PaneKind.Terminal));
        var r = StageProgramLogic.RemoveSub(s, PaneKind.Terminal);

        Assert.False(r.ProgramActive);                      // Main 一人 → 配役終了
        Assert.Equal(PaneKind.Editor, r.Main);
    }
}
