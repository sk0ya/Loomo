using System.Linq;
using sk0ya.Loomo.Core.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>デバッグ起動の引数・環境変数の入力欄を <see cref="DebugLaunchConfig"/> 用に解釈する純粋ヘルパの検証。</summary>
public class DebugLaunchArgsTests
{
    [Fact]
    public void ParseArgs_splits_on_whitespace()
    {
        Assert.Equal(new[] { "--foo", "bar", "42" }, DebugLaunchArgs.ParseArgs("--foo bar 42").ToArray());
    }

    [Fact]
    public void ParseArgs_keeps_quoted_value_as_single_token()
    {
        Assert.Equal(new[] { "--path", "C:\\Program Files\\app" },
            DebugLaunchArgs.ParseArgs("--path \"C:\\Program Files\\app\"").ToArray());
    }

    [Fact]
    public void ParseArgs_supports_empty_quoted_token()
    {
        Assert.Equal(new[] { "--name", "" }, DebugLaunchArgs.ParseArgs("--name \"\"").ToArray());
    }

    [Fact]
    public void ParseArgs_collapses_runs_of_whitespace()
    {
        Assert.Equal(new[] { "a", "b" }, DebugLaunchArgs.ParseArgs("  a    b  ").ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseArgs_empty_input_yields_no_tokens(string? input)
    {
        Assert.Empty(DebugLaunchArgs.ParseArgs(input));
    }

    [Fact]
    public void ParseEnv_parses_key_value_lines()
    {
        var env = DebugLaunchArgs.ParseEnv("FOO=1\nBAR=hello world");
        Assert.NotNull(env);
        Assert.Equal("1", env!["FOO"]);
        Assert.Equal("hello world", env["BAR"]);
    }

    [Fact]
    public void ParseEnv_value_may_contain_equals()
    {
        var env = DebugLaunchArgs.ParseEnv("CONN=Server=.;Db=x");
        Assert.Equal("Server=.;Db=x", env!["CONN"]);
    }

    [Fact]
    public void ParseEnv_trims_key_and_ignores_blank_comment_and_malformed_lines()
    {
        var env = DebugLaunchArgs.ParseEnv("  KEY = v \n\n# comment\nno-equals\n=novalue");
        Assert.NotNull(env);
        Assert.Single(env!);
        Assert.Equal(" v", env["KEY"]);   // 値は最初の '=' 以降をそのまま（行端の空白は行トリムで除去済み）
    }

    [Fact]
    public void ParseEnv_last_duplicate_wins()
    {
        var env = DebugLaunchArgs.ParseEnv("K=1\nK=2");
        Assert.Equal("2", env!["K"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("# only a comment")]
    public void ParseEnv_empty_or_no_pairs_yields_null(string? input)
    {
        Assert.Null(DebugLaunchArgs.ParseEnv(input));
    }
}
