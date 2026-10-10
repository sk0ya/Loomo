using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.Tests;

/// <summary>CamelHumps 一致（<see cref="FuzzyMatcher"/>）。Rider の Go to File と同じく、語の頭を拾うクエリで
/// 狙ったファイルが上に来ること——ただの飛び石一致と同点だと埋もれていた。</summary>
public class CamelHumpsMatchingTests
{
    [Theory]
    [InlineData("EditorLocationHistory.cs", "ELH")]
    [InlineData("EditorLocationHistory.cs", "elh")]
    [InlineData("EditorLocationHistory.cs", "EdLoHi")]
    [InlineData("EditorLocationHistory.cs", "EditorLoc")]
    [InlineData("HTMLParser.cs", "HP")]
    [InlineData("my-file_name.ts", "mfn")]
    public void 先頭の語から当たる(string text, string query)
        => Assert.Equal(FuzzyMatcher.HumpMatch.Leading, FuzzyMatcher.MatchHumps(text, query));

    [Fact]
    public void 途中の語から当たる()
        => Assert.Equal(FuzzyMatcher.HumpMatch.Inner, FuzzyMatcher.MatchHumps("EditorLocationHistory.cs", "LH"));

    [Theory]
    [InlineData("EditorLocationHistory.cs", "EHL")]   // 順番が違う
    [InlineData("EditorLocationHistory.cs", "Eitor")] // 語の頭からでない
    [InlineData("EditorLocationHistory.cs", "E")]     // 1文字は対象外
    public void 当たらない(string text, string query)
        => Assert.Equal(FuzzyMatcher.HumpMatch.None, FuzzyMatcher.MatchHumps(text, query));

    [Fact]
    public void CamelHumpsは飛び石一致より上に並ぶ()
    {
        var hump = FuzzyMatcher.Score("EditorLocationHistory.cs", "ELH");
        var subsequence = FuzzyMatcher.Score("SelectionHelper.cs", "ELH");
        Assert.NotNull(hump);
        Assert.NotNull(subsequence);
        Assert.True(hump < subsequence);
    }

    [Fact]
    public void 先頭からのCamelHumpsは部分一致より上_途中からは下()
    {
        Assert.True(FuzzyMatcher.Score("ShellWindow.cs", "SW") < FuzzyMatcher.Score("Answer.cs", "sw"));
        Assert.True(FuzzyMatcher.Score("Answer.cs", "sw") < FuzzyMatcher.Score("MyShellWindow.cs", "SW"));
    }

    [Fact]
    public void 語の多いパスでも組合せ爆発しない()
    {
        var path = string.Concat(Enumerable.Repeat("aAbBcC/", 60)) + "x.cs";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        FuzzyMatcher.MatchHumps(path, "abcabcabcabcabcz");
        Assert.True(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void パレットもCamelHumpsで並べる()
    {
        var items = new[]
        {
            new PaletteCommand("x", "SelectionHelper", () => { }),
            new PaletteCommand("x", "EditorLocationHistory", () => { }),
        };
        Assert.Equal("EditorLocationHistory", PaletteFilter.Filter(items, "ELH")[0].Title);
    }

    [Fact]
    public void ファイル構造は種類名では絞り込まない()
    {
        var items = new[]
        {
            new PaletteCommand("method", "  Run ()", () => { }) { MatchTitleOnly = true },
            new PaletteCommand("method", "  Method ()", () => { }) { MatchTitleOnly = true },
        };
        Assert.Equal(new[] { "  Method ()" }, PaletteFilter.Filter(items, "meth").Select(i => i.Title));
        Assert.Equal(new[] { "  Run ()" }, PaletteFilter.Filter(items, "Run").Select(i => i.Title));
    }
}

/// <summary>テスト⇔実装の命名規約（<see cref="RelatedTestFiles"/>）。</summary>
public class RelatedTestFilesTests
{
    [Theory]
    [InlineData(@"C:\w\src\Foo.cs", new[] { "FooTests.cs", "FooTest.cs", "Foo.Tests.cs" })]
    [InlineData(@"C:\w\src\foo.ts", new[] { "foo.test.ts", "foo.spec.ts" })]
    [InlineData(@"C:\w\pkg\foo.py", new[] { "test_foo.py", "foo_test.py" })]
    public void 実装からテストの名前(string path, string[] expected)
    {
        Assert.False(RelatedTestFiles.IsTestFile(path));
        Assert.Equal(expected, RelatedTestFiles.CounterpartNames(path));
    }

    [Theory]
    [InlineData(@"C:\w\tests\FooTests.cs", "Foo.cs")]
    [InlineData(@"C:\w\tests\FooTest.cs", "Foo.cs")]
    [InlineData(@"C:\w\tests\Foo.Tests.cs", "Foo.cs")]
    [InlineData(@"C:\w\src\foo.spec.ts", "foo.ts")]
    [InlineData(@"C:\w\pkg\test_foo.py", "foo.py")]
    [InlineData(@"C:\w\pkg\foo_test.py", "foo.py")]
    public void テストから実装の名前(string path, string expectedFirst)
    {
        Assert.True(RelatedTestFiles.IsTestFile(path));
        Assert.Equal(expectedFirst, RelatedTestFiles.CounterpartNames(path)[0]);
    }

    [Fact]
    public void Testsそのものは実装ではない()
    {
        Assert.False(RelatedTestFiles.IsTestFile(@"C:\w\Tests.cs"));
        Assert.Empty(RelatedTestFiles.CounterpartNames(@"C:\w\README.md"));
    }

    [Fact]
    public void 近いフォルダーの相手を先に出す()
    {
        var ranked = RelatedTestFiles.RankByProximity(
            @"C:\w\tests\Loomo.Tests\Palette\PaletteFilterTests.cs",
            new[] { @"C:\w\src\Other\PaletteFilter.cs", @"C:\w\src\Loomo\Palette\PaletteFilter.cs" });
        Assert.Equal(@"C:\w\src\Loomo\Palette\PaletteFilter.cs", ranked[0]);
    }
}
