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

    /// <summary>サーバーの答えを待たずに畳み、あとから foldingRange が届いても開かない。
    /// 実測で Roslyn は using 節を範囲として返さないことがあり、以前はここで畳みが消えて
    /// 「開いた直後に閉じた using が勝手に開く」になっていた。</summary>
    [Fact]
    public void CloseUsingRanges_folds_without_server_ranges_and_survives_them()
    {
        var folds = new FoldManager();

        ShellAppearanceCoordinator.CloseUsingRanges(folds, [new LspFoldingRange(0, 3)]);
        Assert.True(Assert.Single(folds.Folds).IsClosed);

        // サーバーは using 節を報せず、クラスの範囲だけを返してきた。
        folds.SetLspRanges([(5, 9)]);

        Assert.True(Assert.Single(folds.Folds, fold => fold.StartLine == 0).IsClosed);
        Assert.NotNull(folds.GetHidingFold(2));
    }

    /// <summary><c>namespace X { using …; }</c> のように using が他の範囲の内側に居る形。
    /// CreateFold は重なる範囲を作らないので、そのまま CloseFold(先頭行) を呼ぶと
    /// その行の最内フォールド＝namespace が閉じ、ファイルが丸ごと畳まれてしまっていた。
    /// 畳めないなら畳まない——他人のフォールドは触らない。</summary>
    [Fact]
    public void CloseUsingRanges_never_closes_an_enclosing_fold()
    {
        var folds = new FoldManager();
        // 0: namespace X {  1-2: using …  4: class …  9: }
        folds.SetLspRanges([(0, 9), (4, 8)]);

        ShellAppearanceCoordinator.CloseUsingRanges(folds, [new LspFoldingRange(1, 2)]);

        Assert.Equal(2, folds.Folds.Count);    // using の範囲は作られない（重なるので作れない）
        Assert.False(Assert.Single(folds.Folds, fold => fold.StartLine == 0).IsClosed);
        Assert.False(Assert.Single(folds.Folds, fold => fold.StartLine == 4).IsClosed);
        Assert.Null(folds.GetHidingFold(4));   // 本文が隠れていない
    }
}
