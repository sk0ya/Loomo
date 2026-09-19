using sk0ya.Loomo.CSharp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>using 節の範囲は<b>本文だけ</b>から決める（サーバーの foldingRange を待たない）。</summary>
public sealed class CSharpUsingFoldMatcherTests
{
    [Fact]
    public void Find_returns_the_leading_using_block()
    {
        const string text = """
            using System;
            using System.Collections.Generic;

            namespace Sample;

            public sealed class Foo
            {
            }
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(0, range.StartLine);
        Assert.Equal(1, range.EndLine);
    }

    /// <summary>空行やコメントで区切って書く流儀があるので、そこでは固まりを切らない。</summary>
    [Fact]
    public void Find_keeps_groups_separated_by_blank_lines_together()
    {
        const string text = """
            using System;

            // Loomo
            using sk0ya.Loomo.Core;
            using sk0ya.Loomo.Services;

            namespace Sample;
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(0, range.StartLine);
        Assert.Equal(4, range.EndLine);
    }

    /// <summary>namespace ブロックの中に書かれた using も、その固まりだけを畳む
    /// （外側の namespace ごと畳まない）。</summary>
    [Fact]
    public void Find_matches_usings_inside_a_namespace_block()
    {
        const string text = """
            namespace Sample
            {
                using System;
                using System.Linq;

                public sealed class Foo
                {
                }
            }
            """;

        var range = Assert.Single(CSharpUsingFoldMatcher.Find(text));

        Assert.Equal(2, range.StartLine);
        Assert.Equal(3, range.EndLine);
    }

    [Fact]
    public void Find_ignores_using_statements()
    {
        const string text = """
            public sealed class Foo
            {
                public void Run()
                {
                    using (var stream = Open())
                    {
                    }
                    using var other = Open();
                }
            }
            """;

        Assert.Empty(CSharpUsingFoldMatcher.Find(text));
    }

    /// <summary>1 行しかない固まりは畳んでも得がないので返さない。</summary>
    [Fact]
    public void Find_ignores_a_single_using()
    {
        Assert.Empty(CSharpUsingFoldMatcher.Find("using System;\n\nnamespace Sample;\n"));
    }
}
