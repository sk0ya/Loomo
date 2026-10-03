using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 左右並び差分の行から、選んだ範囲の変更行（旧/新の行番号）とステージの状況を引く部分、
/// および中央の帯をステージ済み／未ステージで分ける部分の検証。
/// </summary>
public class DiffSideSelectionTests
{
    // 行0: a（文脈） / 行1: b→B（変更・ステージ済み） / 行2: +c2（追加・未ステージ） / 行3: d（文脈）
    // 行4: -e（削除・未ステージ） / 行5: f（文脈）
    private static readonly DiffSideRowVm[] Rows =
    [
        new("Context", "a", "Context", "a", "1", "1"),
        new("Removed", "b", "Added", "B", "2", "2", LeftStaged: true, RightStaged: true),
        new("Empty", "", "Added", "c2", "", "3"),
        new("Context", "d", "Context", "d", "3", "4"),
        new("Removed", "e", "Empty", "", "4", ""),
        new("Context", "f", "Context", "f", "5", "5"),
    ];

    [Fact]
    public void 選択した行の変更を両側から集めステージ済みかを返す()
    {
        var selection = DiffSideBlockMapper.CollectSelectedChanges(Rows, 1, 1, wholeBlock: false);

        Assert.Equal([2], selection.OldLines);
        Assert.Equal([2], selection.NewLines);
        Assert.True(selection.HasStaged);
        Assert.False(selection.HasUnstaged);
    }

    [Fact]
    public void ステージ済みと未ステージが隣り合えば帯を分ける()
    {
        var blocks = DiffSideBlockMapper.Map(Rows);

        Assert.Equal(3, blocks.Count);
        Assert.Equal((1, 1, DiffStageState.All), (blocks[0].Start, blocks[0].End, blocks[0].Stage));
        Assert.Equal((2, 2, DiffStageState.None), (blocks[1].Start, blocks[1].End, blocks[1].Stage));
        Assert.Equal((4, 4, DiffStageState.None), (blocks[2].Start, blocks[2].End, blocks[2].Stage));
    }

    [Fact]
    public void 片側だけステージ済みの行は一部ステージ()
    {
        DiffSideRowVm[] rows = [new("Removed", "b", "Added", "B", "2", "2", LeftStaged: true, RightStaged: false)];

        Assert.Equal(DiffStageState.Partial, Assert.Single(DiffSideBlockMapper.Map(rows)).Stage);
    }

    [Fact]
    public void 選択が無ければキャレットのある連続した変更丸ごと_ステージの状態では分けない()
    {
        var selection = DiffSideBlockMapper.CollectSelectedChanges(Rows, 2, 2, wholeBlock: true);

        Assert.Equal([2], selection.OldLines);
        Assert.Equal([2, 3], selection.NewLines.Order());
        Assert.True(selection.HasUnstaged);
        Assert.True(selection.HasStaged);
    }

    [Fact]
    public void かたまり全体の状態は混在なら一部()
    {
        var region = DiffSideBlockMapper.RegionOf(Rows, 2);

        Assert.NotNull(region);
        Assert.Equal((1, 2, DiffStageState.Partial), (region.Value.Start, region.Value.End, region.Value.Stage));
        Assert.Null(DiffSideBlockMapper.RegionOf(Rows, 0));
    }

    [Fact]
    public void 逆向きの選択でも範囲として扱い文脈行は数えない()
    {
        var selection = DiffSideBlockMapper.CollectSelectedChanges(Rows, 5, 2, wholeBlock: false);

        Assert.Equal([4], selection.OldLines);
        Assert.Equal([3], selection.NewLines);
    }

    [Fact]
    public void 変更に掛かっていなければ何も無い()
    {
        Assert.True(DiffSideBlockMapper.CollectSelectedChanges(Rows, 0, 0, wholeBlock: true).IsEmpty);
        Assert.True(DiffSideBlockMapper.CollectSelectedChanges(Rows, -1, -1, wholeBlock: false).IsEmpty);
    }
}
