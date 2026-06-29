using sk0ya.Loomo.Services.Search;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>検索結果の一括置換エンジン（ReplaceEngine）の検証。</summary>
public class ReplaceEngineTests
{
    [Fact]
    public void Literal_replaces_all_occurrences_and_counts()
    {
        var (text, count) = ReplaceEngine.Replace("foo bar foo", "foo", "baz", useRegex: false, caseSensitive: true);
        Assert.Equal("baz bar baz", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Literal_case_insensitive_matches_mixed_case()
    {
        var (text, count) = ReplaceEngine.Replace("Foo foo FOO", "foo", "x", useRegex: false, caseSensitive: false);
        Assert.Equal("x x x", text);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Literal_case_sensitive_skips_other_case()
    {
        var (text, count) = ReplaceEngine.Replace("Foo foo", "foo", "x", useRegex: false, caseSensitive: true);
        Assert.Equal("Foo x", text);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Regex_supports_group_substitution()
    {
        var (text, count) = ReplaceEngine.Replace("a1 b2", @"(\w)(\d)", "$2$1", useRegex: true, caseSensitive: true);
        Assert.Equal("1a 2b", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Invalid_regex_is_noop()
    {
        var (text, count) = ReplaceEngine.Replace("abc", "(", "x", useRegex: true, caseSensitive: true);
        Assert.Equal("abc", text);
        Assert.Equal(0, count);
    }

    [Fact]
    public void No_match_returns_zero_and_unchanged_text()
    {
        var (text, count) = ReplaceEngine.Replace("hello", "zzz", "x", useRegex: false, caseSensitive: false);
        Assert.Equal("hello", text);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Empty_query_is_noop()
    {
        var (text, count) = ReplaceEngine.Replace("hello", "", "x", useRegex: false, caseSensitive: false);
        Assert.Equal("hello", text);
        Assert.Equal(0, count);
    }
}
