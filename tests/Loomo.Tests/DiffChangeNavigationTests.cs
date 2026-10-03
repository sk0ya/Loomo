using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ↑↓（次/前の変更）で「いまどの変更にいるか」。キャレットを置くだけでは読み取り専用の左やフォーカスの無い
/// エディタでは見えず、どれに飛んだのか分からなかったので、VM が現在ブロックの先頭と「2 / 7」を持ち、
/// View はそれで枠を引く。ここでは VM 側の正本を固定する。
/// </summary>
public class DiffChangeNavigationTests
{
    private static DiffSessionViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var git = new GitService(workspace);
        return new DiffSessionViewModel(git, new FakeEditorService(), workspace, new DiffFileGateway(),
            new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            new GitCompareBaseViewModel(git));
    }

    /// <summary>変更ブロックが2つ（2行目の1行・5〜6行目の2行）ある比較を開き、行が届くまで待つ。</summary>
    private static async Task<DiffSessionViewModel> OpenTwoBlockComparisonAsync()
    {
        var sut = CreateSut();
        sut.ShowComparison(new DiffComparison(
            "元", "a\nb\nc\nd\ne\nf", "案", "a\nB\nc\nd\nE\nF"));
        var watch = Stopwatch.StartNew();
        while (sut.SideRows.Count == 0 && watch.Elapsed.TotalSeconds < 10)
            await Task.Delay(20);
        Assert.NotEmpty(sut.SideRows);
        return sut;
    }

    [Fact]
    public async Task 飛ぶ前は現在位置を出さない()
    {
        var sut = await OpenTwoBlockComparisonAsync();

        Assert.Equal(-1, sut.CurrentChangeAnchor);
        Assert.Equal("", sut.ChangePositionLabel);
    }

    [Fact]
    public async Task 次の変更へ飛ぶと現在ブロックと何番目かが進む()
    {
        var sut = await OpenTwoBlockComparisonAsync();

        sut.JumpToFirstChange();
        var first = sut.CurrentChangeAnchor;
        Assert.Equal("1 / 2", sut.ChangePositionLabel);
        Assert.Equal("B", sut.SideRows[first].RightText);
        Assert.Equal(first, sut.ChangeBlockEnd(first));   // 1行だけのブロック

        sut.JumpToNextChangeCommand.Execute(null);
        var second = sut.CurrentChangeAnchor;
        Assert.Equal("2 / 2", sut.ChangePositionLabel);
        Assert.Equal("E", sut.SideRows[second].RightText);
        Assert.Equal("F", sut.SideRows[sut.ChangeBlockEnd(second)].RightText);   // 枠はブロックの最後の行まで

        sut.JumpToPrevChangeCommand.Execute(null);
        Assert.Equal(first, sut.CurrentChangeAnchor);
        Assert.Equal("1 / 2", sut.ChangePositionLabel);
    }

    [Fact]
    public async Task 表示形式を切り替えると現在位置は外れる()
    {
        var sut = await OpenTwoBlockComparisonAsync();
        sut.JumpToFirstChange();

        sut.IsSideBySide = false;   // 行の並び（統合／左右）が入れ替わるので、前の添字は指せない

        Assert.Equal(-1, sut.CurrentChangeAnchor);
        Assert.Equal("", sut.ChangePositionLabel);
    }
}
