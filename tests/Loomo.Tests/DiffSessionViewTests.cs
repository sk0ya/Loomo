using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public class DiffSessionViewTests
{
    [Theory]
    [InlineData(300, 500, 400, 300)]
    [InlineData(450, 500, 400, 400)]
    [InlineData(450, 400, 500, 400)]
    [InlineData(-10, 500, 400, 0)]
    public void 横スクロール位置を左右共通の到達可能範囲に収める(
        double requested,
        double leftMaximum,
        double rightMaximum,
        double expected)
    {
        var actual = DiffSessionView.ClampToSharedHorizontalRange(
            requested, leftMaximum, rightMaximum);

        Assert.Equal(expected, actual);
    }
}
