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

    [Fact]
    public void Reports_declaration_file_and_1_based_line()
    {
        // @" の直後の改行で 1 行目は空。Runs は 6 行目、Cases は 10 行目（いずれも宣言の行）。
        var src = @"
namespace N;
public class T
{
    [Fact]
    public void Runs() { }

    [Theory]
    [InlineData(1)]
    public void Cases(int x) { }
}";
        var tests = TestSourceParser.Parse(src, @"C:\work\TTests.cs");
        Assert.Equal(new[] { 6, 10 }, tests.Select(t => t.Line1));
        Assert.All(tests, t => Assert.Equal(@"C:\work\TTests.cs", t.SourcePath));
    }

    [Fact]
    public void Line_is_the_method_declaration_not_the_attribute()
    {
        var src = @"namespace N;
public class T
{
    [Fact]
    public void Runs() { }
}";
        var one = Assert.Single(TestSourceParser.Parse(src, "T.cs"));
        Assert.Equal(5, one.Line1);   // 属性は 4 行目、宣言は 5 行目
    }

    [Fact]
    public void File_is_absent_when_not_given_but_the_line_still_is_reported()
    {
        var src = @"namespace N;
public class T
{
    [Fact] public void Runs() { }
}";
        var one = Assert.Single(TestSourceParser.Parse(src));
        Assert.Null(one.SourcePath);
        Assert.Equal(4, one.Line1);
    }

    /// <summary>1 行で閉じない属性（<c>[InlineData(</c> で改行する Theory）。かつては 2 行目の
    /// "InlineData(" をメソッド名と誤認し、偽の完全名が 2 行目に登録されたうえ本物が拾えなかった
    /// ——ガターに「押しても何も起きない ▶」が出る形。</summary>
    [Fact]
    public void Handles_attributes_that_span_several_lines()
    {
        var src = @"
namespace N;
public class T
{
    [Theory]
    [InlineData(
        1, 2)]
    [InlineData(
        3, 4)]
    public void Adds(int a, int b) { }
}";
        var one = Assert.Single(TestSourceParser.Parse(src, "T.cs"));
        Assert.Equal("N.T.Adds", one.FullyQualifiedName);
        Assert.True(one.IsParameterized);
        Assert.Equal(10, one.Line1);   // 宣言の行（属性の途中ではない）
    }

    [Fact]
    public void Attribute_arguments_containing_brackets_do_not_confuse_the_scan()
    {
        var src = @"
namespace N;
public class T
{
    [Theory]
    [MemberData(nameof(Cases))]
    [InlineData(new[] { 1, 2 })]
    public void Adds(int[] xs) { }
}";
        var one = Assert.Single(TestSourceParser.Parse(src, "T.cs"));
        Assert.Equal("N.T.Adds", one.FullyQualifiedName);
        Assert.Equal(8, one.Line1);
    }

    [Fact]
    public void Tests_after_a_multi_line_attribute_are_still_found()
    {
        var src = @"
namespace N;
public class T
{
    [Theory]
    [InlineData(
        1)]
    public void First(int x) { }

    [Fact]
    public void Second() { }
}";
        var tests = TestSourceParser.Parse(src, "T.cs");
        Assert.Equal(new[] { "N.T.First", "N.T.Second" }, tests.Select(t => t.FullyQualifiedName));
    }

    [Fact]
    public void Attribute_prefix_stripping_carries_depth_across_lines()
    {
        var depth = 0;
        Assert.Equal("", TestSourceParser.StripAttributePrefix("    [InlineData(", ref depth));
        Assert.Equal(1, depth);
        Assert.Equal("", TestSourceParser.StripAttributePrefix("        1, 2)]", ref depth));
        Assert.Equal(0, depth);
        Assert.Equal("public void Adds(int a, int b) { }",
            TestSourceParser.StripAttributePrefix("    public void Adds(int a, int b) { }", ref depth));
        Assert.Equal(0, depth);
    }
}
