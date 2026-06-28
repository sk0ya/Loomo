using System.Linq;
using sk0ya.Loomo.Services.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ソース走査によるテスト探索パーサ（ビルド不要の高速探索）の検証。</summary>
public class TestSourceParserTests
{
    [Fact]
    public void Finds_fact_with_file_scoped_namespace()
    {
        var src = @"
namespace Foo.Bar;

public class WidgetTests
{
    [Fact]
    public void Does_thing() { }
}";
        var tests = TestSourceParser.Parse(src);
        Assert.Equal(new[] { "Foo.Bar.WidgetTests.Does_thing" }, tests.Select(t => t.FullyQualifiedName));
        Assert.False(tests[0].IsParameterized);
    }

    [Fact]
    public void Marks_theory_as_parameterized_and_ignores_inline_data()
    {
        var src = @"
namespace N;
public class T
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Case(int x) { }
}";
        var tests = TestSourceParser.Parse(src);
        var one = Assert.Single(tests);
        Assert.Equal("N.T.Case", one.FullyQualifiedName);
        Assert.True(one.IsParameterized);
    }

    [Fact]
    public void Handles_block_namespace_and_async_and_attribute_on_same_line()
    {
        var src = @"
namespace A.B
{
    public sealed class C
    {
        [Trait(""k"",""v"")]
        [Fact] public async System.Threading.Tasks.Task Runs() { }
    }
}";
        var tests = TestSourceParser.Parse(src);
        Assert.Equal(new[] { "A.B.C.Runs" }, tests.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void Multiple_classes_in_one_file_attribute_to_nearest_class()
    {
        var src = @"
namespace N;
public class First { [Fact] public void A() { } }
public class Second { [Fact] public void B() { } }";
        var tests = TestSourceParser.Parse(src);
        Assert.Equal(new[] { "N.First.A", "N.Second.B" }, tests.Select(t => t.FullyQualifiedName).OrderBy(x => x));
    }

    [Fact]
    public void Ignores_attributes_in_comments_and_strings()
    {
        var src = @"
namespace N;
public class T
{
    // [Fact] public void Commented() { }
    string s = ""[Fact] public void InString() { }"";

    [Fact]
    public void Real() { }
}";
        var tests = TestSourceParser.Parse(src);
        Assert.Equal(new[] { "N.T.Real" }, tests.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void Recognizes_nunit_and_mstest_markers()
    {
        var src = @"
namespace N;
public class T
{
    [Test] public void NUnitOne() { }
    [TestMethod] public void MsOne() { }
}";
        var tests = TestSourceParser.Parse(src);
        Assert.Equal(new[] { "N.T.MsOne", "N.T.NUnitOne" },
            tests.Select(t => t.FullyQualifiedName).OrderBy(x => x));
    }
}
