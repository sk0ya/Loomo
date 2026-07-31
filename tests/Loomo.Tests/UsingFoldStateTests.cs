using Editor.Core.Folds;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class UsingFoldStateTests
{
    [Fact]
    public void CloseUsingRanges_closes_only_the_using_fold()
    {
        var folds = new FoldManager();
        folds.SetLspRanges([(0, 1), (3, 8), (5, 7)]);
        var imports = new LspFoldingRange(0, 1);

        ShellAppearanceCoordinator.CloseUsingRanges(folds, [imports]);

        Assert.True(Assert.Single(folds.Folds, fold => fold.StartLine == 0).IsClosed);
        Assert.False(Assert.Single(folds.Folds, fold => fold.StartLine == 3).IsClosed);
        Assert.False(Assert.Single(folds.Folds, fold => fold.StartLine == 5).IsClosed);
        Assert.NotNull(folds.GetHidingFold(1));
        Assert.Null(folds.GetHidingFold(4));
    }
}
