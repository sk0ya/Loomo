using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>ActivityBar は上段・中段の2本。どの項目がどちらの段に住むかは人間が決め、
/// 段ごとに自分のサイドバー区画を持つ——ここではその「並びの決まり方」だけを見る
/// （区画への載せ替えは <c>ShellWindow.ActivityBar.cs</c>、開閉は <see cref="ShellViewModelTests"/>）。</summary>
public sealed class ActivityBarTests
{
    private static readonly string[] Known = ["explorer", "git", "solution", "pegboard", "tabs"];

    [Fact]
    public void 初回起動は上段がエクスプローラ他で中段はタブ一覧だけ()
    {
        var sut = new ActivityBarViewModel();

        Assert.Equal(["explorer", "git", "solution", "pegboard"], sut.PrimaryItems.Select(i => i.Id));
        Assert.Equal(["tabs"], sut.SecondaryItems.Select(i => i.Id));
        Assert.Equal(ActivityBarSlot.Secondary, sut.SlotOf(SidebarPanel.Tabs));
        Assert.Equal(ActivityBarSlot.Primary, sut.SlotOf(SidebarPanel.Explorer));
    }

    [Fact]
    public void 保存された並びを復元し未知のIdは読み捨てる()
    {
        var (primary, secondary) = ActivityBarViewModel.Arrange(
            Known, ["tabs", "git", "からっぽ"], ["explorer"]);

        // 保存された順がそのまま並びになり、知らない Id は落ちる。
        Assert.Equal(["explorer"], secondary);
        Assert.Equal(["tabs", "git"], primary.Take(2));
        // 保存に無かった項目は消さずに既定の段（既定が中段なのはタブ一覧だけ）の末尾へ落とす
        // ——アプリ更新で項目が増えても消えない。
        Assert.Equal(["tabs", "git", "solution", "pegboard"], primary);
    }

    [Fact]
    public void 重複したIdは先に出た段にだけ置く()
    {
        var (primary, secondary) = ActivityBarViewModel.Arrange(Known, ["tabs"], ["tabs", "git"]);

        Assert.Equal("tabs", primary[0]);          // 2度目の tabs（中段）は捨てる
        Assert.Equal(["git"], secondary);
        Assert.DoesNotContain("tabs", secondary);
        // 保存に無かった残りは既定の段（＝上段）の末尾へ。
        Assert.Equal(["tabs", "explorer", "solution", "pegboard"], primary);
    }

    [Fact]
    public void 段をまたぐ移動と並べ替えができる()
    {
        var sut = new ActivityBarViewModel();
        var tabs = sut.ItemFor(SidebarPanel.Tabs)!;
        var git = sut.ItemFor(SidebarPanel.Git)!;

        Assert.True(sut.Move(tabs, ActivityBarSlot.Primary, 0));
        Assert.Equal(["tabs", "explorer", "git", "solution", "pegboard"], sut.PrimaryItems.Select(i => i.Id));
        Assert.Empty(sut.SecondaryItems);
        Assert.Equal(ActivityBarSlot.Primary, tabs.Slot);

        // 同じ段の中での並べ替え（下へ動かすと抜けたぶん詰まる）。
        Assert.True(sut.Move(git, ActivityBarSlot.Primary, 4));
        Assert.Equal(["tabs", "explorer", "solution", "git", "pegboard"], sut.PrimaryItems.Select(i => i.Id));
        // 自分の前（3）も自分の直後（4）も「いまと同じ場所」なので動かさない。
        Assert.False(sut.Move(git, ActivityBarSlot.Primary, 3));
        Assert.False(sut.Move(git, ActivityBarSlot.Primary, 4));
    }

    /// <summary>一番下へ落としたら一番下に入ること。挿入位置（0〜件数）を件数-1へ丸めていた頃は
    /// 最後から2番目に入り、末尾へは二度と置けなかった。</summary>
    [Fact]
    public void 同じ段の末尾へ落とせる()
    {
        var sut = new ActivityBarViewModel();
        var explorer = sut.ItemFor(SidebarPanel.Explorer)!;

        Assert.True(sut.Move(explorer, ActivityBarSlot.Primary, sut.PrimaryItems.Count));

        Assert.Equal(["git", "solution", "pegboard", "explorer"], sut.PrimaryItems.Select(i => i.Id));
    }

    /// <summary>段をまたいだのか並べ替えただけなのかを通知で区別すること
    /// （並べ替えただけの面を開き直すと、人間がしていないナビゲーションを軌跡へ書く）。</summary>
    [Fact]
    public void 移動の通知は段の変化と並べ替えを区別する()
    {
        var sut = new ActivityBarViewModel();
        var moves = new List<ActivityBarItemMoved>();
        sut.ItemMoved += (_, e) => moves.Add(e);

        sut.Move(sut.ItemFor(SidebarPanel.Explorer)!, ActivityBarSlot.Primary, 2);   // 同じ段
        sut.Move(sut.ItemFor(SidebarPanel.Explorer)!, ActivityBarSlot.Secondary, 0); // 段をまたぐ

        Assert.Equal([false, true], moves.Select(m => m.SlotChanged));
        Assert.All(moves, m => Assert.Equal(SidebarPanel.Explorer, m.Item.Panel));
    }

    [Fact]
    public void 移動は設定へ書き戻る()
    {
        var settings = new LoomoSettings();
        var sut = new ActivityBarViewModel(settings);

        sut.Move(sut.ItemFor(SidebarPanel.Git)!, ActivityBarSlot.Secondary, 0);

        Assert.Equal(["explorer", "solution", "pegboard"], settings.ActivityBar.Primary);
        Assert.Equal(["git", "tabs"], settings.ActivityBar.Secondary);

        // 保存した並びで起動し直すとそのまま戻る。
        var restored = new ActivityBarViewModel(settings);
        Assert.Equal(["git", "tabs"], restored.SecondaryItems.Select(i => i.Id));
    }

    [Fact]
    public void 出せない面はその段の既定候補から外す()
    {
        var sut = new ActivityBarViewModel();
        sut.Move(sut.ItemFor(SidebarPanel.Solution)!, ActivityBarSlot.Secondary, 0);
        sut.SetAvailable(SidebarPanel.Solution, false);

        Assert.False(sut.Holds(ActivityBarSlot.Secondary, SidebarPanel.Solution));
        Assert.Equal(SidebarPanel.Tabs, sut.FirstAvailablePanel(ActivityBarSlot.Secondary));
        Assert.Equal(SidebarPanel.Git, sut.FirstAvailablePanel(ActivityBarSlot.Primary, excluding: SidebarPanel.Explorer));
    }
}
