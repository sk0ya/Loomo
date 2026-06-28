using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Core.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>停止行から自動変数（Autos）の評価候補を拾う純粋ヘルパの検証。netcoredbg に autos スコープが無いので
/// ソース行からの近似抽出が肝。</summary>
public class AutosExtractorTests
{
    [Fact]
    public void Extracts_identifiers_in_order_without_duplicates()
    {
        var c = AutosExtractor.ExtractCandidates("total = price + price + tax;", null);
        Assert.Equal(new[] { "total", "price", "tax" }, c);
    }

    [Fact]
    public void Member_access_yields_root_then_chain()
    {
        var c = AutosExtractor.ExtractCandidates("var x = order.Total;", null).ToList();
        Assert.Contains("order", c);
        Assert.Contains("order.Total", c);
        Assert.True(c.IndexOf("order") < c.IndexOf("order.Total"));  // ルートが先
    }

    [Fact]
    public void Drops_chain_rooted_at_keyword()
    {
        var c = AutosExtractor.ExtractCandidates("this.count = items.Length;", null);
        Assert.DoesNotContain("this", c);
        Assert.DoesNotContain("this.count", c);
        Assert.Contains("items", c);
        Assert.Contains("items.Length", c);
    }

    [Fact]
    public void Filters_keywords()
    {
        var c = AutosExtractor.ExtractCandidates("if (ready) return value;", null);
        Assert.DoesNotContain("if", c);
        Assert.DoesNotContain("return", c);
        Assert.DoesNotContain("value", c);   // value も文脈語として除外
        Assert.Contains("ready", c);
    }

    [Fact]
    public void Ignores_identifiers_inside_strings_and_comments()
    {
        var c = AutosExtractor.ExtractCandidates("name = \"hidden\" + tail; // trailing note", null);
        Assert.Equal(new[] { "name", "tail" }, c);
    }

    [Fact]
    public void Ignores_char_literals()
    {
        var c = AutosExtractor.ExtractCandidates("c = sep == 'x' ? a : b;", null);
        Assert.Equal(new[] { "c", "sep", "a", "b" }, c);
    }

    [Fact]
    public void Includes_previous_line_then_current()
    {
        var c = AutosExtractor.ExtractCandidates("sum += n;", "var n = source;");
        Assert.Equal(new[] { "n", "source", "sum" }, c);
    }

    [Fact]
    public void Collapses_spaces_in_member_chains()
    {
        var c = AutosExtractor.ExtractCandidates("y = a . b . c;", null);
        Assert.Contains("a.b.c", c);
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("\"hello\"", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("(評価エラー: not found)", false)]
    [InlineData("(セッションがありません)", false)]
    public void LooksLikeValue_filters_errors(string? value, bool expected)
    {
        Assert.Equal(expected, AutosExtractor.LooksLikeValue(value));
    }
}
