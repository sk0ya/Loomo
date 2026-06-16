using sk0ya.Loomo.App.Layout;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// レイアウトモードの巡回（Ctrl+T）純ロジックの検証。スクラッチ枠（-1）の有無・前後巡回・ラップ・
/// 列外からの復帰を押さえる。
/// </summary>
public class LayoutCycleLogicTests
{
    [Theory]
    // スクラッチ無し・保存3枚：0→1→2→0 と前進、ラップする
    [InlineData(0, 3, false, 1, 1)]
    [InlineData(1, 3, false, 1, 2)]
    [InlineData(2, 3, false, 1, 0)]
    // 後進：0→2→1→0
    [InlineData(0, 3, false, -1, 2)]
    [InlineData(2, 3, false, -1, 1)]
    public void Cycles_saved_layouts_without_scratch(int current, int count, bool hasScratch, int direction, int expected)
        => Assert.Equal(expected, LayoutCycleLogic.NextIndex(current, count, hasScratch, direction));

    [Theory]
    // スクラッチ有り・保存3枚：列 = [-1, 0, 1, 2]、前進で -1→0→1→2→-1
    [InlineData(-1, 3, true, 1, 0)]
    [InlineData(2, 3, true, 1, -1)]
    // 後進で -1→2（先頭から末尾へラップ）
    [InlineData(-1, 3, true, -1, 2)]
    [InlineData(0, 3, true, -1, -1)]
    public void Cycles_with_scratch_slot(int current, int count, bool hasScratch, int direction, int expected)
        => Assert.Equal(expected, LayoutCycleLogic.NextIndex(current, count, hasScratch, direction));

    [Fact]
    public void Empty_cycle_returns_current_unchanged()
        => Assert.Equal(-1, LayoutCycleLogic.NextIndex(-1, 0, hasScratch: false, direction: 1));

    [Fact]
    public void Scratch_only_stays_on_scratch()
        => Assert.Equal(-1, LayoutCycleLogic.NextIndex(-1, 0, hasScratch: true, direction: 1));

    [Theory]
    // 現在位置が列に無い（削除済み等）：前進で先頭、後進で末尾へ入る
    [InlineData(5, 3, false, 1, 0)]
    [InlineData(5, 3, false, -1, 2)]
    [InlineData(5, 3, true, 1, -1)]
    [InlineData(5, 3, true, -1, 2)]
    public void Recovers_when_current_not_in_cycle(int current, int count, bool hasScratch, int direction, int expected)
        => Assert.Equal(expected, LayoutCycleLogic.NextIndex(current, count, hasScratch, direction));
}
