using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ブランチツリーのクリック判定。メニューを開いたまま別のブランチをダブルクリックすると、1回目は
/// メニューを閉じるのに使われて WPF に届かず、2回目が単クリックとして届く——その2回目を
/// ダブルクリックと見なす物差し（Windows のダブルクリックと同じ時間と幅）。
/// </summary>
public sealed class GitBranchTreeClickPolicyTests
{
    [Theory]
    [InlineData(150, 0, 0, true)]
    [InlineData(500, 2, -2, true)]      // 時間・幅とも境界ちょうどはダブルクリック
    [InlineData(501, 0, 0, false)]      // 遅い＝メニューを閉じてから改めて押した単クリック
    [InlineData(150, 3, 0, false)]      // 別の行・別の場所を押した
    [InlineData(150, 0, -3, false)]
    [InlineData(-1, 0, 0, false)]
    public void メニューを閉じたクリックに続く押下をダブルクリックと見なすのは時間と幅の内だけ(
        long elapsed, int dx, int dy, bool expected)
        => Assert.Equal(expected,
            GitBranchTreeClickPolicy.IsDoubleClickAfterMenuDismiss(elapsed, dx, dy, 500, 4, 4));

    [Fact]
    public void ダブルクリックの2回目は単クリックにせずメニューの予約を取り消す()
        => Assert.Equal(GitBranchTreeClickAction.CancelPendingMenu,
            GitBranchTreeClickPolicy.Resolve(2, onExpansionToggle: false, hasTreeNode: true, isFolder: false));
}
