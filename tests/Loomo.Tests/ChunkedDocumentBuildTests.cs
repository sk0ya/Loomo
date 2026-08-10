using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 差分本体を分割して組むときの不変条件。<b>スライスに割っても、一息に組んだのと同じ文書ができる</b>
/// ——行が1つでも抜けたり二重に入ったりすると、差分の行と FlowDocument の段落の添字がずれ、
/// 「この行をエディタで開く」「次の変更へジャンプ」「選択した行を破棄」がすべて別の行を指す。
/// </summary>
public class ChunkedDocumentBuildTests
{
    /// <summary>組まれた行を出現順に記録する（実際の追記のかわり）。</summary>
    private static ChunkedDocumentBuild Recording(int rowCount, List<int> built)
        => new(rowCount, (start, end) =>
        {
            for (var i = start; i < end; i++) built.Add(i);
        });

    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 250)]
    [InlineData(7, 3)]
    [InlineData(250, 250)]
    [InlineData(251, 250)]
    [InlineData(1000, 1)]
    [InlineData(1000, 250)]
    [InlineData(1000, 999_999)]
    public void スライスに割っても全行が順番どおり1回ずつ組まれる(int rowCount, int chunk)
    {
        var built = new List<int>();
        var build = Recording(rowCount, built);

        while (build.IsRunning) build.Step(chunk);

        Assert.Equal(Enumerable.Range(0, rowCount), built);
    }

    [Fact]
    public void 組み終えたら走らなくなる()
    {
        var built = new List<int>();
        var build = Recording(5, built);

        build.Step(5);
        Assert.False(build.IsRunning);

        build.Step(5); // 予約済みのスライスが後から走っても二重に組まない
        Assert.Equal([0, 1, 2, 3, 4], built);
    }

    [Fact]
    public void 行が無いときは最初から走らない()
    {
        var built = new List<int>();

        var build = Recording(0, built);

        Assert.False(build.IsRunning);
        Assert.Empty(built);
    }

    [Fact]
    public void 打ち切ったら以降は1行も組まない()
    {
        var built = new List<int>();
        var build = Recording(100, built);
        build.Step(10);

        build.Cancel();
        build.Step(10);
        build.Finish();

        Assert.Equal(Enumerable.Range(0, 10), built);   // 打ち切り前の10行だけ
        Assert.False(build.IsRunning);                  // 待たせ続けない
        Assert.True(build.Cancelled);
    }

    [Fact]
    public void 追いつかせると残り全部が組まれる()
    {
        var built = new List<int>();
        var build = Recording(1000, built);
        build.Step(250);

        build.Finish();

        Assert.Equal(Enumerable.Range(0, 1000), built);
        Assert.False(build.IsRunning);
    }

    [Fact]
    public void 追いつかせた後にスライスが走っても二重に組まない()
    {
        var built = new List<int>();
        var build = Recording(600, built);
        build.Step(250);
        build.Finish();

        build.Step(250); // Dispatcher に積んであった続き

        Assert.Equal(Enumerable.Range(0, 600), built);
    }
}
